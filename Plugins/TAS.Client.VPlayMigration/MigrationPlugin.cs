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

    public sealed class Plugin : IUiPlugin
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
        private static bool Ready(IMedia media) => media != null && media.MediaStatus == TMediaStatus.Available && media.FileExists();
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
                    if (action == "Import") {
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
                Reconcile(); _lastError = null;
            } catch (Exception ex) {
                _lastError = ex.Message;
                try { Save("response.json", new { Success = false, Error = _lastError, Utc = DateTime.UtcNow });
                    var request = Within(Path.Combine(Home, "request.json")); if (File.Exists(request)) File.Move(request, Within(Path.Combine(Home, "rejected-" + DateTime.UtcNow.Ticks + ".json")));
                } catch { /* Never mutate runtime when diagnostics cannot be persisted. */ }
            }
        }
        private static string Token(string id) => string.Concat(id.Where(char.IsLetterOrDigit));
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
        private void Reconcile()
        {
            var path = Within(Path.Combine(Home, "events.json")); var catalogPath = Within(Path.Combine(Home, "catalog.json"));
            if (!File.Exists(path) || !File.Exists(catalogPath)) return;
            var sourceEvents = JArray.Parse(File.ReadAllText(path)); var catalog = JArray.Parse(File.ReadAllText(catalogPath));
            var native = Events().Where(e => e.IdAux != null && (e.IdAux.StartsWith("VP26E:") || e.IdAux.StartsWith("VP26T:"))).ToArray();
            var local = Engine.MediaManager.MediaDirectoryPRI;
            var media = local.GetAllFiles().ToArray();
            var status = new List<object>();
            foreach (JObject asset in catalog) {
                var key = (string)asset["Key"]; var name = (string)asset["LogicalName"];
                var matches = media.Where(m => string.Equals(m.MediaName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var state = matches.Length > 1 ? "Ambiguous" : "Missing"; IMedia found = matches.SingleOrDefault(m => matches.Length == 1);
                if (found != null) { asset["MediaGuid"] = found.MediaGuid.ToString(); state = Ready(found) ? "Ready" : "Ingesting"; }
                else if (Guid.TryParse((string)asset["MediaGuid"], out var id) && id != Guid.Empty) {
                    var archived = Engine.MediaManager.ArchiveDirectory?.Find(id); if (Ready(archived)) state = "Archived / Restore Pending";
                }
                var idText = (string)asset["MediaGuid"];
                foreach (JObject se in sourceEvents.Where(s => (string)s["AssetKey"] == key)) {
                    var eventId = ((string)se["ItemId"]).Replace("-", ""); var sequence = (int)se["Sequence"];
                    var review = (string)se["ParseStatus"] != "Pending";
                    if (Guid.TryParse(idText, out var guid) && guid != Guid.Empty) foreach (var e in native.Where(e => e.IdAux.EndsWith(eventId, StringComparison.Ordinal)))
                        if (e.MediaGuid != guid && e.PlayState == TPlayState.Scheduled) { e.MediaGuid = guid; e.Save(); }
                    status.Add(new { Sequence = sequence, Begin = (string)se["Begin"], Title = (string)se["Title"], EditorialTitle = (string)se["EditorialTitle"], Source = (string)se["OriginalSource"], MediaGuid = idText, Status = review ? "Special Event / Requires Review" : state, MatchStatus = (string)se["MatchStatus"] });
                }
            }
            foreach (var se in sourceEvents.Where(s => (string)s["Kind"] != "File")) status.Add(new { Sequence = (int)se["Sequence"], Status = "Special Event / Requires Review" });
            Save("catalog.json", catalog); Save("readiness.json", new { Utc = DateTime.UtcNow, Error = _lastError, Events = status, Ready = status.Count(s => (string)JObject.FromObject(s)["Status"] == "Ready"), Total = sourceEvents.Count });
        }
    }
}
