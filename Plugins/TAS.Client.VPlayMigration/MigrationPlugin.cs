// EWTN 2026-11-09. Local opt-in migration plugin. GPL-2.0.
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TAS.Client.Common.Plugin;
using TAS.Common;
using TAS.Common.Interfaces;
using TAS.Common.Interfaces.Media;
using TAS.Server.Media;

namespace TAS.Client.VPlayMigration
{
    [Export(typeof(IUiPluginFactory))]
    public sealed class Factory : IUiPluginFactory
    {
        public Type Type => typeof(Plugin);
        public object[] Create(IUiPluginContext context) => context is IUiEngine ? new object[] { new Plugin((IUiEngine)context) } : new object[0];
    }

    public sealed partial class Plugin : IUiPlugin
    {
        // Must be created with private ACL by deployment; no network listener or service.
        internal static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"EWTN\Playout\Migration");
        private readonly IUiEngine _context;
        private readonly DispatcherTimer _timer;
        private readonly Item _menu;
        private string _lastError;
        public Plugin(IUiEngine context)
        {
            _context = context; _menu = new Item(this);
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, __) => Tick(), Dispatcher.CurrentDispatcher);
            _absoluteTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, __) => AbsoluteTick(), Dispatcher.CurrentDispatcher);
        }
        public IUiPluginContext Context => _context;
        public IUiMenuItem Menu => _menu;
        private IEngine Engine => _context.Engine;
        private sealed class Item : UiMenuItemBase
        {
            private readonly Plugin _owner;
            internal Item(Plugin owner) : base(owner) { _owner = owner; Header = "VPlay migration: refresh readiness"; }
            public override bool CanExecute(object parameter) => true;
            public override void Execute(object parameter) => _owner.Tick();
        }
        private static void SafeHome()
        {
            if (!Directory.Exists(Home) || (File.GetAttributes(Home) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private migration directory unavailable");
        }
        internal static string Within(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path outside migration workspace");
            for (var p = full; !string.Equals(p, Home, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse path rejected");
            return full;
        }
        private static void Save(string name, object data)
        {
            var path = Within(Path.Combine(Home, name)); var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(data, Formatting.Indented));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        private IEnumerable<IEvent> Events()
        {
            var seen = new HashSet<IEvent>(); var stack = new Stack<IEvent>(Engine.GetRootEvents());
            while (stack.Count != 0) { var e = stack.Pop(); if (e == null || e.IsDeleted || !seen.Add(e)) continue; yield return e; foreach (var child in e.GetSubEvents()) stack.Push(child); var next = e.GetNext(); if (next != null) stack.Push(next); }
        }
        private bool Idle => Engine.Playing == null && !Events().Any(e => e.PlayState == TPlayState.Playing || e.PlayState == TPlayState.Fading);
        private static bool Ready(IMedia media) => media != null && media.MediaStatus == TMediaStatus.Available && media.FileExists() && TAS.Server.PlayoutCacheRules.Ready(media);
        private void Tick()
        {
            if (!File.Exists(Path.Combine(Home, "enabled"))) return;
            try {
                SafeHome();
                if (!Engine.HaveRight(EngineRight.Rundown)) throw new UnauthorizedAccessException("Rundown rights required");
                var requestPath = Within(Path.Combine(Home, "request.json"));
                if (File.Exists(requestPath)) {
                    var request = JObject.Parse(File.ReadAllText(requestPath));
                    var action = (string)request["Action"];
                    if (action == "StartAbsoluteSchedule") {
                        var root=Events().Single(e=>(e.IdAux??"").StartsWith("VP26R:"));
                        root.EventName="VPLAY EWTN MAIN [ABSOLUTE Europe/Kyiv]";root.IsEnabled=true;root.Save();
                        Save("absolute-enabled.json",new{Enabled=true,Root=root.IdAux});
                        _absoluteSources=null;AbsoluteTick();
                    } else if (action == "RemoveExperimental") {
                        if (!Idle) throw new IOException("Stop playback before experimental cleanup");
                        if (!(Engine is TAS.Server.Engine localEngine) || localEngine.ArchivePolicy != TArchivePolicyType.NoArchive) throw new IOException("Cleanup requires NoArchive-on-delete policy to protect media");
                        if ((bool?)request["VerifiedBackup"] != true) throw new IOException("Verified backup required");
                        var wanted = ((JArray)request["Roots"]).Cast<JObject>().ToArray();
                        var roots = Engine.GetRootEvents().ToArray(); var selected = new List<IEvent>();
                        foreach (var item in wanted) {
                            var root = roots.SingleOrDefault(e => e.Id == (ulong)item["Id"] && e.EventName == (string)item["Name"]);
                            if (root == null || (root.IdAux ?? "").StartsWith("VP26", StringComparison.Ordinal)) throw new IOException("Cleanup identity changed or migration root selected");
                            selected.Add(root);
                        }
                        var nodes = selected.SelectMany(Descendants).Distinct().ToArray();
                        if (nodes.Any(e => e.PlayState == TPlayState.Playing || e.PlayState == TPlayState.Fading || e.PlayState == TPlayState.Paused)) throw new IOException("Protected event still active");
                        Save("cleanup-before.json", new { Utc = DateTime.UtcNow, Events = nodes.Select(e => new { e.Id, e.IdAux, e.EventName, e.MediaGuid }).ToArray() });
                        foreach (var root in selected) DeleteExperimental(root);
                        if (selected.Any(e => !e.IsDeleted)) throw new IOException("Native deletion incomplete; retained events require review");
                    } else if (action == "RestoreCheck") {
                        if (!Idle || System.Diagnostics.Process.GetProcessesByName("ffmpeg").Length != 0) throw new IOException("Restore check requires idle and no encoder");
                        var id = Guid.Parse((string)request["MediaGuid"]);
                        var local = Engine.MediaManager.MediaDirectoryPRI as ServerDirectory;
                        var archive = Engine.MediaManager.ArchiveDirectory as ArchiveDirectory;
                        var media = local?.FindMediaByMediaGuid(id) as ServerMedia;
                        var archived = archive?.Find(id) as ArchiveMedia;
                        if (!Ready(media) || !Ready(archived) || !media.MediaName.StartsWith("vplay_", StringComparison.Ordinal)) throw new IOException("Owned ready media and archive required");
                        // Require all physical producers to be clear, including orphan producers after restart.
                        if (Environment.MachineName != "AIR-02" || Engine.PlayoutChannelPRI.Id != 1 || Engine.Preview?.Channel.Id != 2) throw new IOException("Restore check is scoped to verified AIR-02 channels 1/2");
                        foreach (var channel in new [] {1,2}) using (var client = new System.Net.Sockets.TcpClient()) {
                            var connecting = client.ConnectAsync("127.0.0.1", 5250);
                            if (!connecting.Wait(1500)) throw new IOException("Caspar unavailable");
                            var stream = client.GetStream(); stream.ReadTimeout = 1500;
                            var command = System.Text.Encoding.UTF8.GetBytes("INFO " + channel + "\r\n"); stream.Write(command, 0, command.Length);
                            var data = new System.Text.StringBuilder(); var buffer = new byte[16384];
                            while (!data.ToString().Contains("</channel>")) { var n = stream.Read(buffer, 0, buffer.Length); if (n == 0 || data.Length > 1048576) throw new IOException("Invalid Caspar state"); data.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, n)); }
                            if (data.ToString().Contains("<filename>")) throw new IOException("Clear Program/Preview/preload before restore check");
                        }
                        var hash = FileHash(media.FullPath);
                        if (hash != FileHash(archived.FullPath)) throw new IOException("Archive hash not confirmed");
                        var hold = @"D:\EWTN\Lab\VPlayRestoreHold"; Directory.CreateDirectory(hold);
                        if ((File.GetAttributes(hold) & FileAttributes.ReparsePoint) != 0 || Path.GetPathRoot(hold) != Path.GetPathRoot(media.FullPath)) throw new IOException("Invalid restore hold root");
                        var held = Path.Combine(hold, id.ToString("N") + ".mov"); if (File.Exists(held)) throw new IOException("Prior restore hold retained; review before retry");
                        Save("restore-check.json", new { MediaGuid = id, Original = media.FullPath, Held = held, Hash = hash, Utc = DateTime.UtcNow });
                        media.MediaStatus = TMediaStatus.Required; media.Save();
                        File.Move(media.FullPath, held); // Required status preserves GUID when watcher observes absence.
                        var container = Engine.GetRootEvents().SingleOrDefault(e => e.IdAux == "VP26PREPARATION");
                        if (container == null) { container = Engine.CreateNewEvent(eventType:TEventType.Container, startType:TStartType.Manual, eventName:"VPLAY preparation references [MANUAL]", idAux:"VP26PREPARATION"); Engine.AddRootEvent(container); container.Save(); }
                        var reference = Engine.CreateNewEvent(videoLayer:VideoLayer.Program,eventType:TEventType.Movie,startType:TStartType.Manual,scheduledTime:DateTime.UtcNow.AddMinutes(5),duration:TimeSpan.FromSeconds(1),mediaGuid:id,eventName:"Restore verification reference",idAux:"VP26P:"+id.ToString("N"));
                        container.InsertUnder(reference,false);reference.Save();
                    } else if (action == "RecoverTrace") {
                        if (!Idle) throw new IOException("Trace recovery requires idle");
                        var rows = JArray.Parse(File.ReadAllText(Within(Path.Combine(Home, "native-links-recovery.json"))));
                        var events = Events().ToDictionary(e => e.Id);
                        foreach (JObject row in rows) {
                            if (!events.TryGetValue((ulong)row["Id"], out var e) || e.EventName != (string)row["EventName"] || e.Duration.Ticks != (long)row["DurationTicks"] || e.ScheduledTime.Ticks != (long)row["ScheduledTicks"]) throw new IOException("Native trace recovery identity mismatch");
                            if (!((string)row["IdAux"] ?? "").StartsWith("VP26", StringComparison.Ordinal)) throw new IOException("Nonmigration trace rejected");
                        }
                        foreach (JObject row in rows) { var e = events[(ulong)row["Id"]]; e.IdAux = (string)row["IdAux"]; e.Save(); }
                    } else if (action == "Import") {
                        var path = Within(Path.Combine(Home, (string)request["File"]));
                        if (!path.EndsWith(".rundown", StringComparison.OrdinalIgnoreCase)) throw new IOException("Expected .rundown");
                        var node = JObject.Parse(File.ReadAllText(path)); Validate(node, true, 0);
                        var allIds = node.SelectTokens("$..IdAux").Select(t => (string)t).ToArray();
                        if (allIds.Distinct(StringComparer.Ordinal).Count() != allIds.Length) throw new IOException("Duplicate source event identity");
                        string fingerprint;
                        using (var hash = System.Security.Cryptography.SHA256.Create()) using (var input = File.OpenRead(path)) fingerprint = BitConverter.ToString(hash.ComputeHash(input));
                        var id = (string)node["IdAux"]; var existing = Events().Where(e => e.IdAux == id).ToArray();
                        if (existing.Length > 1) throw new IOException("Duplicate imported root identity");
                        // Commit marker after completed tree. On restart a partial owned tree is retained for review.
                        if (existing.Length == 1) {
                            var marker = Within(Path.Combine(Home, "import-" + Token(id) + ".json"));
                            if (!File.Exists(marker)) throw new IOException("Incomplete prior import retained; review before retry");
                            if ((string)JObject.Parse(File.ReadAllText(marker))["Fingerprint"] != fingerprint) throw new IOException("Schedule identity exists with different content; explicit revision review required");
                        } else {
                            var created = Create(node, null, null, true);
                            Save("import-" + Token(id) + ".json", new { IdAux = id, Id = created.Id, Fingerprint = fingerprint, CompletedUtc = DateTime.UtcNow });
                        }
                    } else if (action == "ResetTest") {
                        if (!Idle) throw new IOException("Test reset requires idle");
                        var id=(string)request["IdAux"];if(id==null||!id.StartsWith("VP26TEST:"))throw new IOException("Only test roots may be rescheduled");
                        var root=Events().Single(e=>e.IdAux==id);Engine.ReSchedule(root);root.ScheduledTime=Engine.CurrentTime.AddSeconds(30);root.Save();
                        foreach(var e in Descendants(root))e.Save();
                    } else if (action == "PlayTest") {
                        if (!Idle) throw new IOException("Existing playback active; test start refused");
                        var id = (string)request["IdAux"];
                        if (id == null || !id.StartsWith("VP26TEST:", StringComparison.Ordinal)) throw new IOException("Only a migration test root can start");
                        var root = Events().Single(e => e.IdAux == id); var movies = Descendants(root).Where(e => e.EventType == TEventType.Movie).ToArray();
                        if (movies.Length < 2 || movies.Any(e => !Ready(e.Media))) throw new IOException("All test media must be Ready");
                        if (!Engine.HaveRight(EngineRight.Play)) throw new UnauthorizedAccessException("Play rights required");
                        Engine.Start(root);
                    } else if (action != "Refresh") throw new IOException("Unknown migration request");
                    Save("response.json", new { Success = true, Action = action, Utc = DateTime.UtcNow }); File.Delete(requestPath);
                }
                // Reconciliation shares the UI dispatcher with the 200ms air
                // clock. Defer noncritical inventory work near an original
                // boundary; current-slot readiness is checked independently.
                if(ReconcileAllowed())Reconcile();
                _lastError = null;
            } catch (Exception ex) {
                _lastError = ex.Message;
                try { Save("response.json", new { Success = false, Error = _lastError, Utc = DateTime.UtcNow });
                    var request = Within(Path.Combine(Home, "request.json")); if (File.Exists(request)) File.Move(request, Within(Path.Combine(Home, "rejected-" + DateTime.UtcNow.Ticks + ".json")));
                } catch { /* Never mutate runtime when diagnostics cannot be persisted. */ }
            }
        }
        private static string Token(string id) => string.Concat(id.Where(char.IsLetterOrDigit));
        private static string FileHash(string path) { using(var sha=System.Security.Cryptography.SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(stream)); }
        private void DeleteExperimental(IEvent root)
        {
            // Native Container refuses deletion while nonempty. Empty children first;
            // native Event.Delete touches events only, and NoArchive guard excludes media moves.
            foreach (var child in root.GetSubEvents().ToArray()) {
                var current = child;
                while (current != null) { var next = current.GetNext(); DeleteExperimental(current); current = next; }
            }
            if (!root.AllowDelete()) throw new IOException("Native event deletion refused: " + root.Id);
            root.Delete();
        }
        internal static void Validate(JObject node, bool root, int depth)
        {
            if (depth > 4) throw new IOException("Tree depth exceeds supported migration layout");
            var type = (TEventType)(int)node["EventType"]; var start = (TStartType)(int)node["StartType"];
            if (type != TEventType.Container && type != TEventType.Rundown && type != TEventType.Movie) throw new IOException("Unsupported migration event type");
            if (root && start != TStartType.Manual) throw new IOException("Root must be manual");
            if (!Enum.IsDefined(typeof(TStartType), start) || start == TStartType.None || start == TStartType.WithParentFromEnd) throw new IOException("Unsupported start semantics");
            if (start == TStartType.OnFixedTime && (bool?)node["IsEnabled"] == true) throw new IOException("Historical fixed-time events must remain disabled");
            if (!((string)node["IdAux"] ?? "").StartsWith("VP26", StringComparison.Ordinal)) throw new IOException("Missing source identity");
            if (!(node["SubEvents"] is JArray)) throw new IOException("Explicit SubEvents array required");
            if (!TimeSpan.TryParse((string)node["Duration"], out var duration) || duration < TimeSpan.Zero) throw new IOException("Invalid duration");
            if (type == TEventType.Movie && duration.Ticks % 400000 != 0) throw new IOException("Movie duration must align to 25fps frames");
            if (type == TEventType.Movie && ((JArray)node["SubEvents"]).Count != 0) throw new IOException("Nested movie actions require review");
            if (type == TEventType.Rundown) {
                var children = (JArray)node["SubEvents"];
                for (var i = 0; i < children.Count; i++) if ((TStartType)(int)children[i]["StartType"] != (i == 0 ? TStartType.WithParent : TStartType.After)) throw new IOException("Invalid sequential block links");
            }
            foreach (var child in (JArray)node["SubEvents"]) Validate((JObject)child, false, depth + 1);
        }
        private IEvent Create(JObject node, IEvent parent, IEvent prior, bool root)
        {
            var source = node.ToObject<EventProxy>();
            var e = Engine.CreateNewEvent(videoLayer: source.Layer, eventType: source.EventType,
                startType: source.StartType, playState: TPlayState.Scheduled, scheduledTime: source.ScheduledTime.ToUniversalTime(),
                duration: source.Duration, scheduledTC: source.ScheduledTc, eventName: source.EventName,
                idAux: source.IdAux, isEnabled: source.IsEnabled, isHold: source.IsHold, isLoop: source.IsLoop, isCGEnabled: false);
            if (root) Engine.AddRootEvent(e); else if (prior != null && source.StartType == TStartType.After) prior.InsertAfter(e); else parent.InsertUnder(e, false);
            e.IsHold = source.IsHold; e.IsEnabled = source.IsEnabled;
            e.Save(); IEvent last = null;
            foreach (JObject child in (JArray)node["SubEvents"]) last = Create(child, e, last, false);
            return e;
        }
        private IEnumerable<IEvent> Descendants(IEvent root)
        {
            var seen = new HashSet<IEvent>(); var stack = new Stack<IEvent>(); stack.Push(root);
            while (stack.Count > 0) { var e = stack.Pop(); if (!seen.Add(e)) continue; yield return e; foreach (var child in e.GetSubEvents()) stack.Push(child); if (e != root && e.GetNext() != null) stack.Push(e.GetNext()); }
        }
        internal static bool CanReconcile(DateTime now,DateTime? next)
            =>!next.HasValue||next.Value-now>TimeSpan.FromSeconds(2);
        internal static string NativeSourceIdentity(string id)
            =>id!=null&&id.Length>=32&&(id.StartsWith("VP26E:",StringComparison.Ordinal)||id.StartsWith("VP26T:",StringComparison.Ordinal))?id.Substring(id.Length-32):null;
        private bool _reconciling;
        private bool ReconcileAllowed()
        {
            var now=Engine.CurrentTime;var next=_absoluteSources?.FirstOrDefault(s=>s.Begin>now);
            return !((Engine as TAS.Server.Engine)?.AbsoluteScheduleEnabled==true)||CanReconcile(now,next?.Begin);
        }
        private void Reconcile()
        {
            if(_reconciling)return;
            var elapsed=System.Diagnostics.Stopwatch.StartNew();
            var path = Within(Path.Combine(Home, "events.json")); var catalogPath = Within(Path.Combine(Home, "catalog.json"));
            if (!File.Exists(path) || !File.Exists(catalogPath)) return;
            var sourceEvents = JArray.Parse(File.ReadAllText(path)); var catalog = JArray.Parse(File.ReadAllText(catalogPath));
            var native = Events().Where(e => e.IdAux != null && (e.IdAux.StartsWith("VP26E:") || e.IdAux.StartsWith("VP26T:"))).ToArray();
            var local = Engine.MediaManager.MediaDirectoryPRI;
            var media = local.GetAllFiles().ToArray();
            // Preserve original ordering, duplicate groups and exact identity
            // matching without rescanning every native event for every source.
            var sourcesByAsset=sourceEvents.Cast<JObject>().ToLookup(s=>(string)s["AssetKey"],StringComparer.Ordinal);
            var nativeByIdentity=native.ToLookup(e=>NativeSourceIdentity(e.IdAux),StringComparer.Ordinal);
            var mediaByName=media.ToLookup(m=>m.MediaName,StringComparer.OrdinalIgnoreCase);
            var status = new List<object>();
            var assets=catalog.GetEnumerator();var dispatcher=Dispatcher.CurrentDispatcher;
            _reconciling=true;int slices=0;double maximumSlice=0;var setupMilliseconds=elapsed.Elapsed.TotalMilliseconds;
            Action step=null;
            step=()=>{
             try{
              // All native objects remain on their original dispatcher. Yield
              // inventory I/O between small slices so the air timer can run.
              if(!ReconcileAllowed()){
                var wait=new DispatcherTimer(DispatcherPriority.Background,dispatcher){Interval=TimeSpan.FromMilliseconds(200)};
                wait.Tick+=(_,__)=>{wait.Stop();step();};wait.Start();return;
              }
              var budget=System.Diagnostics.Stopwatch.StartNew();slices++;
              while(assets.MoveNext()){
                var asset=(JObject)assets.Current;
                var key = (string)asset["Key"]; var name = (string)asset["LogicalName"];
                var matches = mediaByName[name].Where(m=>string.Equals(m.MediaName,name,StringComparison.OrdinalIgnoreCase)).ToArray();
                var state = matches.Length > 1 ? "Ambiguous" : "Missing"; IMedia found = matches.SingleOrDefault(m => matches.Length == 1);
                if (found != null) { asset["MediaGuid"] = found.MediaGuid.ToString(); state = Ready(found) ? "Ready" : found.MediaStatus == TMediaStatus.Available && found.FileExists() ? "Not Ready / Cache Pending" : "Ingesting"; }
                else if (Guid.TryParse((string)asset["MediaGuid"], out var id) && id != Guid.Empty) {
                    var archived = Engine.MediaManager.ArchiveDirectory?.Find(id); if (Ready(archived)) state = "Archived / Restore Pending";
                }
                var idText = (string)asset["MediaGuid"];
                foreach (JObject se in sourcesByAsset[key]) {
                    var eventId = ((string)se["ItemId"]).Replace("-", ""); var sequence = (int)se["Sequence"];
                    var review = (string)se["ParseStatus"] != "Pending";
                    if (Guid.TryParse(idText, out var guid) && guid != Guid.Empty) foreach (var e in nativeByIdentity[eventId])
                        if (!e.IsDeleted && NativeSourceIdentity(e.IdAux)==eventId && e.MediaGuid != guid && e.PlayState == TPlayState.Scheduled) { e.MediaGuid = guid; e.Save(); }
                    status.Add(new { Sequence = sequence, Begin = (string)se["Begin"], Title = (string)se["Title"], EditorialTitle = (string)se["EditorialTitle"], Source = (string)se["OriginalSource"], MediaGuid = idText, Status = review ? "Special Event / Requires Review" : state, MatchStatus = (string)se["MatchStatus"] });
                }
                if(budget.ElapsedMilliseconds>=25){
                    maximumSlice=Math.Max(maximumSlice,budget.Elapsed.TotalMilliseconds);
                    dispatcher.BeginInvoke(DispatcherPriority.Background,step);return;
                }
              }
            maximumSlice=Math.Max(maximumSlice,budget.Elapsed.TotalMilliseconds);
            foreach (var se in sourceEvents.Where(s => (string)s["Kind"] != "File")) status.Add(new { Sequence = (int)se["Sequence"], Status = "Special Event / Requires Review" });
            Save("catalog.json", catalog); Save("readiness.json", new { Utc = DateTime.UtcNow, Error = _lastError, Events = status, Ready = status.Count(s => (string)JObject.FromObject(s)["Status"] == "Ready"), Total = sourceEvents.Count });
            Save("readiness-metrics.json",new{Utc=DateTime.UtcNow,ElapsedMilliseconds=elapsed.Elapsed.TotalMilliseconds,SetupMilliseconds=setupMilliseconds,MaximumSliceMilliseconds=maximumSlice,Slices=slices,Assets=catalog.Count,SourceEvents=sourceEvents.Count,NativeEvents=native.Length});
            assets.Dispose();_reconciling=false;
             }catch(Exception ex){
                assets.Dispose();_reconciling=false;_lastError=ex.Message;
                try{Save("readiness-metrics.json",new{Utc=DateTime.UtcNow,Error=ex.ToString()});}catch{}
                // Retain the last complete report. Never change Program or
                // approve incomplete readiness after an inventory failure.
             }
            };
            dispatcher.BeginInvoke(DispatcherPriority.Background,step);
        }
    }
}
