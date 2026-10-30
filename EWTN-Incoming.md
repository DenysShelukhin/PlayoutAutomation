# EWTN managed Incoming (ewtn.4)

Extends the existing engine, native IngestOperation/FileManager and ArchiveAutomation.
No service, manifest, schema migration, external encoder pipeline or sender application.

Settings are stored in the existing engine Config JSON and exposed in the existing
Archive/schedule preparation configurator section. IncomingEnabled defaults false;
IncomingQuietSeconds defaults 30 (minimum 5); IncomingCleanupEnabled defaults true.
IncomingFolder, IncomingIngestProfileName and IncomingWorkFolder select the three
dedicated flat roots. IncomingJournal is internal, not operator-editable.

Use a dedicated flat BroadcastReadyV1 WatchFolder profile, DeleteSource=false.
Incoming and the managed input directory must share a volume; work and Server Media
must share a volume. Keep the three new roots disjoint from Server Media. Restrict
internal directories to the TVPlay identity, SYSTEM and administrators. Share only
Incoming, granting explicitly authorized Windows accounts; no anonymous/Everyone access.

The engine reconciles Incoming once per second (at most 1000 supported files/pass).
FileSystemWatcher events are hints. Admission waits for unchanged size/mtime, then
checks exclusive access, ffprobe readability and an exclusive identity/hash lease.
A journaled by-handle, no-overwrite NTFS rename claims the input. This does not prove
delivery completeness after a sender closes a truncated yet decodable file. Failed
probe inputs are held until changed; failed native encodes are retained for review
or corrected re-delivery. No unbounded automatic encode retry loop is started.

Native Ingest writes outside the watched Server Media directory, runs with -xerror,
verifies metadata/duration and fully decodes the output. A durable revision intent
then publishes the immutable version under its existing MediaGuid. Logical identity
is the extensionless filename, NFC-normalized and case-insensitive, matched against
MediaName. Ambiguous names are held; no arbitrary existing material is chosen.

Program, preload, Preview, disconnected channels and queued native media operations
defer replacement. A read-only AMCP check additionally detects producers surviving
a TVPlay restart. Versions shorter than scheduled cuts are held for operator review;
existing event durations/timecodes are never silently rewritten. Existing metadata
and rundown GUID references remain intact. Archive-only identities retain their GUID.

Server and Archive versions use unique physical names, so an open old producer is
never overwritten. SQLite commit readback is mandatory before input cleanup. The
legacy provider can swallow INSERT/UPDATE failures: GUID/row-ID readback protects
against its last_insert_rowid behavior. Archive revision registration and SHA must
also confirm before previous local/archive physical files are retired. Identical
archive content can be reused. Restore of a stale archive is held while its revision
intent is pending. The native archive/restore queues remain the only copy mechanism.

All cleanup uses native FileManager operations with root, NTFS identity and SHA guards.
Cleanup failure retries cleanup only. No global DeleteSource flag is enabled. Reparse
paths, directories, ADS and hard-linked inputs are refused. No cache eviction occurs.

Crash recovery reloads engine JSON; orphan/external FFmpeg processes delay encoding
until they exit. Interrupted native Ingest restarts from its owned input. Published
versions and archive intents are reconciled without creating another logical GUID.
The Media tab shows Incoming states/errors alongside existing archive diagnostics.
The existing native operation history contains encoder and cleanup output.

Build using the existing Windows/.NET Framework toolchain. Changed managed projects:
TAS.Server, TAS.Client, TAS.Client.Config, TAS.Remoting.Proxy, TVPlay, TVPlay.config.
No changes to CasparCG, FFmpeg, native wrappers or SQLite schema. Regression harness:
EWTN.Archive.Tests.exe <new disposable test directory> and Ingest.Tests.exe.

Before deployment close TVPlay/configurator normally, require no active rundown or
FFmpeg jobs, and verify application/config/SQLite/archive-metadata backups. For rollback,
disable Incoming, retain owned inputs/revision files/journals, close applications, and
restore the previous managed binaries/profile. Keep current media/SQLite; older builds
ignore Incoming settings. Retain the complete closed snapshot for maintenance recovery.
