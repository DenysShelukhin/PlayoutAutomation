# EWTN VPlay migration candidate

2026-11-09. Additive, opt-in client plugin; no core DLL/schema change, no remote
listener and no TVPlayClient requirement. This is a candidate until native import
and playout have been accepted on AIR-02. The existing Incoming/BRv1/Archive path
is reused; this plugin does not encode, copy source video, delete media or evict cache.

## Import contract

The input is an EventProxy-compatible `.rundown` JSON tree. This narrow loader is
needed because the original loader drops loop/start semantics and cannot retain
arbitrary nested fixed-time entries. It creates and inserts events exclusively
through `IEngine.CreateNewEvent`, `AddRootEvent`, `IEvent.InsertUnder/InsertAfter`
and `Save`. It never writes SQLite directly.

Only Container, Rundown and Movie are accepted. Roots must be Manual; historical
fixed-time children must remain disabled. Duration must be frame aligned for
25fps movies, and sequential block links are validated. Source items are traced
through `IdAux=VP26E:<source item GUID>`; test copies use `VP26T:`. Unknown/live
events are retained as disabled review entries, never synthesized as black/silence.
Do not claim equivalent playback for reviewed entries.

The converter/preparation scripts live in the private infrastructure
repository; original XML, Excel, source paths/titles and output JSON are never
published here. They preserve a private source hash, raw event XML, audio config,
original times and cut fields. Editorial suggestions never cross dates and cannot
be high confidence when explicit episode numbers conflict or lack corroboration.

## Local opt-in workspace

Deployment must create `C:\ProgramData\EWTN\Playout\Migration` with a private ACL
for the TVPlay user, SYSTEM and Administrators. The plugin runs only when `enabled`
exists there. Workspace files: `events.json`, `catalog.json`, converted `.rundown`
files. `request.json` accepts:

```json
{"Action":"Import","File":"vplay-original.rundown"}
```

The input must be directly within that workspace or a contained non-reparse path.
There is no network API. The result is `response.json`. An import completion marker
makes repeated imports idempotent; an incomplete native tree is retained for review,
not silently duplicated or deleted. Reconciliation binds stable catalog names to
native MediaGuid, resolves archived GUIDs and writes `readiness.json`.

`PlayTest` accepts only `VP26TEST:` roots, refuses existing playing/fading events,
requires Play rights and all test media Ready. No automatic play is performed on
import/startup. Historical inventory remains disabled. The UI menu only refreshes
readiness. Fixed-time scheduling must be separately reviewed before enabling it.

## Build and tests

Use the existing .NET 4.8 x64 managed toolchain. Restore/build
`Plugins/TAS.Client.VPlayMigration/TAS.Client.VPlayMigration.csproj`, then
`tests/VPlay.Tests/VPlay.Tests.csproj`. The test executable takes the private full
historical and manual test `.rundown` paths; it checks acceptance and safety rejects.
It does not substitute for actual native import, restart or NDI playout acceptance.

Install only `TAS.Client.VPlayMigration.dll` in the existing Plugins directory,
after an idle-window check and verified application/SQLite backup. It references
the installed compatible TAS assemblies. Normal TVPlay restart is required to load
MEF; never restart a playing engine without an explicitly approved test window.

Rollback: stop/close in the same approved idle window, preserve current SQLite,
remove only this plugin from the active Plugins directory (retain the binary
privately), disable its marker, and restart the unchanged core application.
Imported events and prepared media remain in the current database. Do not restore
an older database over subsequently prepared MediaGuid/archive entries.

## C: cache dependency

Changing only the primary media root from D: to C: is unsafe: Managed Incoming
currently publishes by same-volume handle rename from its D: work directory.
A future bounded cache needs an atomic cross-volume staging copy and verified
publication, a shared reservation budget for every publisher (including legacy
ingest and Restore), and eviction protected against Program/Preview/preload,
native operations and upcoming schedule use. Eviction must retain native archive
identity/GUID, require a confirmed archive and cover restart/orphan handling.
This candidate does not enforce a 200GB runtime cache and does not change media root.
