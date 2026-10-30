# EWTN native Archive automation (1.8.9390-ewtn.3)

Extends the existing engine, MediaManager, ArchiveDirectory, IngestOperation,
CopyOperation and file-operation queues. No service, storage database, schema
migration, cache eviction or Incoming deletion is introduced. GPL-2.0 applies.

## Operator configuration

In TVPlay.config, select the rundown engine and expand **Automatic Archive /
schedule preparation**. Stop TVPlay before saving configuration.

| Setting | Compatible default | Meaning |
|---|---|---|
| AutoArchiveAfterIngest | false | Copy successfully ingested movies to the selected native Archive; retain both local and Incoming copies. |
| AutoRestoreEnabled | false | Prepare missing scheduled movies from the selected Archive. |
| RestoreHorizonHours | 72 | Positive hours ahead; includes a ten-minute late-event grace period. |
| RestoreEntireRundown | false | Inspect all enabled, scheduled/paused/playing movie events in the engine's loaded roots, including manual roots, regardless of dates. |
| RestoreMaxConcurrentJobs | 1 | Native Restore lane concurrency, validated 1–16. Other native lanes retain their existing limits. |

AIR-02 acceptance enables the first two switches, leaves the horizon at 72,
entire-rundown off and concurrency one. Existing installations opt in explicitly.
The native movie selector has **Include Archive**; selection itself does not
restore. Existing media type, frame-rate, category and expiration filters apply.
Archived results remain selectable when the local cache is absent.

## Execution and readiness

Before native Ingest writes its result, its GUID intent is stored in the existing
engine Config JSON. Successful Ingest makes that intent eligible for native Copy
to Archive. Its native Success acknowledgement is durable and separate from
retry phase: a decodable but shortened interrupted output is never inferred to
be completed. Unconfirmed results require operator review; an explicitly made,
verified matching native Archive copy can retire their intent. GUID-prefix archive
filenames and a durable reserved destination prevent duplicate copies after a
restart; logical name, GUID, timecodes and other native media properties persist.
Existing GUID/content conflicts fail without overwrite. Journal write failures
retain the intent and report failure rather than silently retiring work.

Event saves/deletes coalesce a schedule refresh; a 30-second periodic traversal
uses the engine's lazily loaded event graph. A one-second worker services queues;
it does not poll the complete SQLite schedule each second. Work is deduplicated
by MediaGuid, sorted by nearest scheduled time and re-derived after restart.
Removing an event cancels work only when no remaining eligible event needs its
GUID. Finished cache files are retained. Full-rundown mode includes old loaded
manual test roots; remove/disable obsolete events to avoid irrelevant warnings.

Restore uses the existing CopyOperation in a bounded native lane. Before queueing,
free space must cover the file, active reservations and a 64 MiB reserve. This is
a preflight check, not a volume quota: concurrent unrelated writers can still
exhaust the disk; normal copy errors then enter delayed retry. Automatic jobs use
one native attempt, with coordinator retries 30/60/120/240/480/600 seconds, capped
at ten minutes. A process restart re-derives Restore requests and can retry sooner.

Copy writes a GUID-specific `.partial` in the destination volume, computes source
and destination SHA-256, flushes, then atomically renames without overwrite.
Native media verification and SQLite registration follow. Missing server-media
placeholders retain their original GUID. Copying/failed placeholders are not
playable. Program and Preview reject movie LOAD until status is Available and
the physical file size matches. Normal cancellation cleans only owned staging;
a crash may leave staging, which a subsequent identical operation replaces.
Restart starts a fresh copy, not byte-level transfer resumption.

The existing Media view displays Pending/Restoring/Ready/Failed, GUID, due time,
error and retry. The engine status bar warns about preparation failures and
marks material due within ten minutes URGENT. Archive failure keeps the local
file; Restore failure never publishes partial content. No automatic eviction,
Move to Archive, archived-file deletion or NAS connection is performed.

## Build and rollback

Use the existing `build/Build-Managed.ps1` toolchain with .NET Framework 4.8
references and the unchanged native wrapper. Changed deployed components are
TAS.Server.dll, TAS.Client.dll, TAS.Client.Config.dll, TVPlay.exe and
TVPlay.config.exe. TAS.Remoting.Proxy is also built for matching remote clients,
but AIR-02's in-process installation does not contain or require that DLL.
AssemblyVersion stays 1.8.9390.2 for binary compatibility; file/informational
version is 1.8.9390.3 / 1.8.9390-ewtn.3. FFmpeg/CasparCG/plugins remain unchanged.

Before replacement, stop native jobs and rundown, close TVPlay normally, take
a consistent SQLite backup and hash the application/configuration snapshot.
Rollback only with TVPlay closed. Restore the five previous binaries and verify
their recorded hashes. Keep the current SQLite database by default; the old
application can use it because the schema is unchanged. Extra engine settings
are ignored by the previous build. Do not save engine settings with an older
configurator while pending journal intents exist. Restoring the old database
is a separate, explicit recovery action which discards later records; never
do it automatically. Keep archive and source files intact.

## Checks

`tests/Archive.Tests` uses a caller-provided disposable directory: 23 checks for
horizon boundaries, whole-rundown mode, reservations/overflow, retry backoff,
journal serialization, actual atomic SHA copy, no-overwrite/idempotency,
cancellation cleanup and actual bounded concurrent native operations.
Run existing `tests/Ingest.Tests` as the Broadcast Ready regression suite.
Runtime test evidence and the deployment-specific rollback receipt live in the
EWTN infrastructure repository, not in this source tree.
