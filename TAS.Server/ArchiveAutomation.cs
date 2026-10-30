// EWTN 2026-10-30. Existing engine/SQLite Config + native archive file operations. GPL-2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using TAS.Common;
using TAS.Common.Interfaces;
using TAS.Server.Media;
using TAS.Server.MediaOperation;

namespace TAS.Server
{
    [DataContract]
    internal sealed class ArchiveIntent
    {
        [DataMember] public Guid Guid;
        [DataMember] public string Name;
        [DataMember] public string Phase;
        [DataMember] public string Folder;
        [DataMember] public string File;
        [DataMember] public int Attempts;
        [DataMember] public long RetryUtcTicks;
        [DataMember] public string Error;
    }

    internal sealed class ArchiveAutomation : IDisposable
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private readonly Engine _engine;
        private readonly MediaManager _manager;
        private readonly object _sync = new object();
        private readonly Dictionary<Guid, ArchiveIntent> _archive = new Dictionary<Guid, ArchiveIntent>();
        private readonly Dictionary<Guid, Work> _restore = new Dictionary<Guid, Work>();
        private readonly Dictionary<Guid, CopyOperation> _activeArchives = new Dictionary<Guid, CopyOperation>();
        private readonly Dictionary<Guid, CopyOperation> _activeRestores = new Dictionary<Guid, CopyOperation>();
        private readonly Dictionary<Guid, string> _history = new Dictionary<Guid, string>();
        private readonly Timer _timer;
        private List<IEvent> _events = new List<IEvent>();
        private int _dirty = 1, _running;
        private DateTime _lastScan;
        private bool _disposed;
        private string _journalError;

        private sealed class Work
        {
            internal Guid Guid;
            internal string Name, State = "Pending", Error;
            internal DateTime Due, Retry;
            internal int Attempts;
        }

        internal ArchiveAutomation(Engine engine, MediaManager manager)
        {
            _engine = engine; _manager = manager;
            if (engine.RestoreHorizonHours <= 0 || engine.RestoreMaxConcurrentJobs < 1 || engine.RestoreMaxConcurrentJobs > 16)
            {
                manager.SetAutomationStatus("FAILED: positive restore hours and 1–16 concurrent jobs required; automation disabled.");
                return;
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(engine.ArchiveAutomationJournal))
                    using (var input = new MemoryStream(Encoding.UTF8.GetBytes(engine.ArchiveAutomationJournal)))
                        foreach (var item in (List<ArchiveIntent>)new DataContractJsonSerializer(typeof(List<ArchiveIntent>)).ReadObject(input))
                            _archive[item.Guid] = item;
            }
            catch (Exception e)
            {
                // Never silently discard an unreadable durable journal.
                manager.SetAutomationStatus("FAILED: unreadable Archive journal; automation disabled. " + e.Message);
                Log.Error(e, "Archive journal invalid");
                return;
            }
            FileManager.Current.RestoreMaxConcurrentJobs = engine.RestoreMaxConcurrentJobs;
            FileManager.Current.OperationPreparing += OperationAdded;
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
        }

        internal void ScheduleChanged() => Interlocked.Exchange(ref _dirty, 1);

        private void OperationAdded(object sender, FileOperationEventArgs args)
        {
            if (!_engine.AutoArchiveAfterIngest || !(args.Operation is IngestOperation ingest) || ingest.DestDirectory != _manager.MediaDirectoryPRI)
                return;
            ingest.DestinationPrepared += RememberIngest;
            ingest.Success += IngestCompleted;
            ingest.Failure += IngestFailed;
        }

        private void RememberIngest(IngestOperation op)
        {
            if (!(op.Dest is ServerMedia media) || media.MediaType != TMediaType.Movie || media.DoNotArchive) return;
            lock (_sync)
            {
                if (_disposed) return;
                if (!_archive.ContainsKey(media.MediaGuid))
                    _archive.Add(media.MediaGuid, new ArchiveIntent { Guid = media.MediaGuid, Name = media.MediaName, Phase = "Ingesting" });
                Persist(); // durable intent before native ingest writes the result
            }
        }

        private void IngestCompleted(object sender, EventArgs args)
        {
            var op = (IngestOperation)sender;
            Detach(op);
            lock (_sync)
            {
                if (_disposed || op.Dest == null) return;
                if (_archive.TryGetValue(op.Dest.MediaGuid, out var item))
                {
                    item.Phase = "Pending"; item.Error = null; item.RetryUtcTicks = 0;
                    TryPersist();
                }
            }
            ScheduleChanged();
        }

        private void IngestFailed(object sender, EventArgs args)
        {
            var op = (IngestOperation)sender; Detach(op);
            lock (_sync)
            {
                if (_disposed || op.Dest == null) return;
                _archive.Remove(op.Dest.MediaGuid);
                _history[op.Dest.MediaGuid] = "Archive Failed: native Ingest did not finish; source retained";
                TryPersist();
            }
        }

        private void Detach(IngestOperation op)
        { op.DestinationPrepared -= RememberIngest; op.Success -= IngestCompleted; op.Failure -= IngestFailed; }

        private void Persist()
        {
            using (var output = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(List<ArchiveIntent>)).WriteObject(output, _archive.Values.ToList());
                _engine.ArchiveAutomationJournal = Encoding.UTF8.GetString(output.ToArray());
            }
            DatabaseProvider.Database.UpdateEngine(_engine); // existing Config JSON, no schema/new database
            _journalError = null;
        }

        private bool TryPersist()
        {
            try { Persist(); return true; }
            catch (Exception e) { Log.Error(e, "Archive journal save failed"); _journalError = "FAILED: Archive journal not saved. Local media retained. " + e.Message; _manager.SetAutomationStatus(_journalError); return false; }
        }

        private void Tick()
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            try
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    if (!(_manager.MediaDirectoryPRI is ServerDirectory initialized) || !initialized.IsInitialized) return;
                    if (Interlocked.Exchange(ref _dirty, 0) != 0 || (DateTime.UtcNow - _lastScan).TotalSeconds >= 30)
                    {
                        Scan(); _lastScan = DateTime.UtcNow;
                    }
                    RestorePending();
                    ArchivePending();
                    PublishStatus();
                }
            }
            catch (Exception e) { Log.Error(e, "Archive automation pass failed"); _manager.SetAutomationStatus("FAILED: automatic media preparation: " + e.Message); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        private void Scan()
        {
            var found = new List<IEvent>();
            var seen = new HashSet<IEvent>();
            var stack = new Stack<IEvent>(_engine.GetRootEvents());
            while (stack.Count != 0)
            {
                var e = stack.Pop();
                if (e == null || !seen.Add(e) || e.IsDeleted) continue;
                found.Add(e);
                foreach (var child in e.GetSubEvents()) stack.Push(child);
                var next = e.GetNext(); if (next != null) stack.Push(next);
            }
            _events = found;
            var needed = _engine.AutoRestoreEnabled
                ? found.Where(e => e.IsEnabled && !e.IsDeleted && e.EventType == TEventType.Movie && e.MediaGuid != Guid.Empty &&
                    (e.PlayState == TPlayState.Scheduled || e.PlayState == TPlayState.Paused || e.PlayState == TPlayState.Playing) &&
                    ArchivePreparationRules.InWindow(_engine.CurrentTime, e.ScheduledTime, _engine.RestoreHorizonHours, _engine.RestoreEntireRundown))
                    .GroupBy(e => e.MediaGuid).ToDictionary(g => g.Key, g => g.OrderBy(e => e.ScheduledTime).First())
                : new Dictionary<Guid, IEvent>();
            foreach (var id in _restore.Keys.Where(g => !needed.ContainsKey(g)).ToArray())
            {
                if (_restore[id].State == "Failed") _history[id] = "Restore Failed (event removed/outside window): " + _restore[id].Name + " — " + _restore[id].Error;
                _restore.Remove(id);
                if (_activeRestores.TryGetValue(id, out var active)) active.Abort();
            }
            foreach (var pair in needed)
            {
                if (!_restore.TryGetValue(pair.Key, out var w))
                    _restore.Add(pair.Key, w = new Work { Guid = pair.Key });
                w.Due = pair.Value.ScheduledTime; w.Name = pair.Value.EventName;
            }
        }

        private static bool Ready(MediaBase media)
        {
            return media != null && media.MediaStatus == TMediaStatus.Available &&
                (media.MediaType != TMediaType.Movie || media.DurationPlay > TimeSpan.Zero) && media.FileExists() &&
                (ulong)new FileInfo(media.FullPath).Length == media.FileSize && media.FileSize != 0;
        }

        private bool OtherCopy(Guid id, object destination)
        {
            return FileManager.Current.GetOperationQueue().OfType<CopyOperation>().Any(o => o.Source?.MediaGuid == id &&
                o.DestDirectory == destination && !o.IsAborted &&
                (o.OperationStatus == FileOperationStatus.InProgress || o.OperationStatus == FileOperationStatus.Waiting));
        }

        private static DateTime Retry(int attempts) => ArchivePreparationRules.NextRetry(DateTime.UtcNow, attempts);

        private void RestorePending()
        {
            if (!_engine.AutoRestoreEnabled || !(_manager.MediaDirectoryPRI is ServerDirectory local)) return;
            foreach (var w in _restore.Values.OrderBy(w => w.Due).ToArray())
            {
                if (_activeRestores.ContainsKey(w.Guid)) continue;
                if (Ready(local.FindMediaByMediaGuid(w.Guid))) { w.State = "Ready"; w.Error = null; w.Retry = DateTime.MinValue; w.Attempts = 0; continue; }
                if (w.State == "Ready") w.State = "Pending";
                if (OtherCopy(w.Guid, local)) { w.State = "Restoring"; continue; }
                if (w.Retry > DateTime.UtcNow || _activeRestores.Count >= _engine.RestoreMaxConcurrentJobs) continue;
                try
                {
                    var archive = _manager.ArchiveDirectory as ArchiveDirectory;
                    if (archive == null || !archive.DirectoryExists) throw new IOException("Archive unavailable");
                    var source = archive.Find(w.Guid) as ArchiveMedia;
                    if (!Ready(source)) throw new IOException("Material absent or invalid in Archive: " + w.Guid);
                    local.RefreshVolumeInfo();
                    if (!ArchivePreparationRules.HasSpace(source.FileSize, local.VolumeFreeSize, _activeRestores.Values.Select(o => o.Source.FileSize)))
                        throw new IOException("Insufficient free space on playout disk (including active reservations and 64MiB reserve)");
                    var op = archive.PrepareAutomationCopy(source, local);
                    _activeRestores.Add(w.Guid, op); w.State = "Restoring"; w.Error = null;
                    op.Finished += (_, __) => FinishRestore(w, op, local);
                    archive.QueueAutomationCopy(op);
                }
                catch (Exception e) { Fail(w, e.Message); }
            }
        }

        private void FinishRestore(Work w, CopyOperation op, ServerDirectory local)
        {
            lock (_sync)
            {
                if (_activeRestores.TryGetValue(w.Guid, out var current) && ReferenceEquals(current, op))
                    _activeRestores.Remove(w.Guid);
                if (_disposed || !_restore.TryGetValue(w.Guid, out var requested) || !ReferenceEquals(requested, w)) return;
                if (op.OperationStatus == FileOperationStatus.Finished && Ready(local.FindMediaByMediaGuid(w.Guid)))
                { w.State = "Ready"; w.Error = null; _engine.NotifyMediaVerified(new MediaEventArgs(op.Dest)); }
                else Fail(w, "Native Restore did not finish Ready: " + string.Join("; ", op.OperationOutput.AsEnumerable().Reverse().Take(2).Reverse()));
            }
        }

        private void Fail(Work w, string error)
        {
            w.State = "Failed"; w.Error = error; w.Retry = Retry(++w.Attempts);
            Log.Warn("Auto Restore {0} Failed; retry {1:o}: {2}", w.Guid, w.Retry, error);
        }

        private void ArchivePending()
        {
            if (!_engine.AutoArchiveAfterIngest || !(_manager.MediaDirectoryPRI is ServerDirectory local)) return;
            foreach (var item in _archive.Values.ToArray())
            {
                if (_activeArchives.ContainsKey(item.Guid) || item.RetryUtcTicks > DateTime.UtcNow.Ticks) continue;
                if (FileManager.Current.GetOperationQueue().OfType<IngestOperation>().Any(o => o.Dest?.MediaGuid == item.Guid &&
                    !o.IsAborted && (o.OperationStatus == FileOperationStatus.Waiting || o.OperationStatus == FileOperationStatus.InProgress))) continue;
                try
                {
                    var source = local.FindMediaByMediaGuid(item.Guid) as ServerMedia;
                    if (!Ready(source))
                    {
                        if (item.Phase == "Ingesting") throw new IOException("Ingest interrupted/not confirmed complete; no partial result archived");
                        throw new IOException("Local completed Ingest is not available");
                    }
                    var archive = _manager.ArchiveDirectory as ArchiveDirectory;
                    if (archive == null || !archive.DirectoryExists) throw new IOException("Archive unavailable; local copy retained");
                    var existing = archive.Find(item.Guid) as ArchiveMedia;
                    if (Ready(existing))
                    {
                        if (existing.FileSize != source.FileSize || AtomicMediaCopy.Hash(existing.FullPath) != AtomicMediaCopy.Hash(source.FullPath))
                            throw new IOException("Archive GUID/content conflict; archive will not be overwritten");
                        _archive.Remove(item.Guid); _history[item.Guid] = "Archive Ready: " + item.Name; Persist(); continue;
                    }
                    if (OtherCopy(item.Guid, archive)) continue;
                    // One native simple-copy lane; retain other intents as Pending, not a large duplicate queue.
                    if (_activeArchives.Count != 0) continue;
                    archive.RefreshVolumeInfo();
                    if (!ArchivePreparationRules.HasSpace(source.FileSize, archive.VolumeFreeSize, new ulong[0]))
                        throw new IOException("Insufficient Archive free space; local copy retained");
                    var op = archive.PrepareAutomationCopy(source, archive, item.Folder, item.File);
                    item.Folder = op.Dest.Folder; item.File = op.Dest.FileName; item.Phase = "Copying"; item.Error = null;
                    Persist(); // remember reserved target across process restarts
                    _activeArchives.Add(item.Guid, op);
                    op.Finished += (_, __) => FinishArchive(item, op, archive);
                    archive.QueueAutomationCopy(op);
                }
                catch (Exception e)
                {
                    _archive[item.Guid] = item; // including retirement persistence failures
                    item.Phase = "Failed"; item.Error = e.Message; item.RetryUtcTicks = Retry(++item.Attempts).Ticks;
                    Log.Warn("Auto Archive {0} Failed: {1}; local retained", item.Guid, e.Message); TryPersist();
                }
            }
        }

        private void FinishArchive(ArchiveIntent item, CopyOperation op, ArchiveDirectory archive)
        {
            lock (_sync)
            {
                _activeArchives.Remove(item.Guid);
                if (_disposed) return;
                if (op.OperationStatus == FileOperationStatus.Finished && Ready(archive.Find(item.Guid) as ArchiveMedia))
                { _archive.Remove(item.Guid); _history[item.Guid] = "Archive Ready: " + item.Name; }
                else
                { item.Phase = "Failed"; item.Error = "Native archive copy not confirmed/registered Ready"; item.RetryUtcTicks = Retry(++item.Attempts).Ticks; }
                if (!TryPersist())
                {
                    _archive[item.Guid] = item;
                    item.Phase = "Failed"; item.Error = _journalError;
                    item.RetryUtcTicks = Retry(++item.Attempts).Ticks;
                }
            }
        }

        private void PublishStatus()
        {
            var warnings = _restore.Values.Where(w => w.State != "Ready").OrderBy(w => w.Due)
                .Select(w => (w.Due <= _engine.CurrentTime.AddMinutes(10) ? "URGENT " : "") +
                    "Restore " + w.State + ": " + w.Name + " [" + w.Guid + "] due " + w.Due.ToString("u") +
                    (w.Error == null ? "" : " — " + w.Error + "; retry " + w.Retry.ToString("u")));
            var archives = _archive.Values.Select(i => "Archive " + i.Phase + ": " + i.Name + " [" + i.Guid + "] " + i.Error);
            var header = "Auto Archive " + (_engine.AutoArchiveAfterIngest ? "ON" : "OFF") + "; Restore " +
                (_engine.AutoRestoreEnabled ? (_engine.RestoreEntireRundown ? "entire loaded rundown" : _engine.RestoreHorizonHours + "h horizon") : "OFF") +
                "; Pending/Restoring/Ready/Failed=" + string.Join("/", new[] { "Pending", "Restoring", "Ready", "Failed" }.Select(s => _restore.Values.Count(w => w.State == s)));
            _manager.SetAutomationStatus(header + "\n" + (_journalError == null ? "" : _journalError + "\n") + string.Join("\n", warnings.Concat(archives).Concat(_history.Values).Take(30)));
            _manager.SetAutomationWarning(_journalError ?? warnings.FirstOrDefault() ?? archives.FirstOrDefault() ?? "");
            if (_history.Count > 50) _history.Remove(_history.Keys.First());
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true; _timer?.Dispose();
                FileManager.Current.OperationPreparing -= OperationAdded;
                foreach (var op in _activeRestores.Values.Concat(_activeArchives.Values).ToArray()) op.Abort();
            }
        }
    }
}
