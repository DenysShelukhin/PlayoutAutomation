# AIR-02 native Ingest capacity candidate — 12 November 2026

Status: built and tested offline; **not installed and not accepted for two live
native encoders**. The working AIR-02 application retains its previous server
binary and one encoder. Its absolute Program switches Ready materials normally.
Existing NDI transport holds remain unresolved. Do not restart a healthy Program
merely to try this candidate or interrupt a native encoding/QC/Archive operation.

The owner authorized a maximum of two native lanes, conditional on identity,
SQLite, Archive, publication, air-time priority and Program acceptance. A short
two-CLI benchmark is not that acceptance. No FFmpeg or driver update is included.

## Candidate behavior

The conversion queue supports a hot capacity increase; lowering capacity drains
running work without aborting it. Incoming admission and that queue share the
same 1–2 limit. Missing/invalid policy, another host or Strict mode defaults to
one lane. Other file-operation queues retain their existing independent limits.

An opt-in `Migration/native-ingest-lanes.json` under the existing ProgramData
playout home has this schema (it is deliberately not created by the build):

```json
{"Version":1,"Host":"AIR-02","MaxParallelJobs":1,"SecondLaneJobIds":[]}
```

For a bounded acceptance, `MaxParallelJobs:2` and a list of existing eligible
native job IDs allow only those jobs into the extra lane. The primary lane keeps
normal current/future priority. Empty `SecondLaneJobIds` permits any eligible
second job. Never exceed two. Return `MaxParallelJobs` to one to stop further
second-lane admissions; already running jobs finish normally. No Program restart
is needed for this policy change **after** this candidate has been installed.

Per-job work paths remain unique. Same-name revisions are serialized by original
creation order plus GUID tie-break. Native coordinator publication callbacks and
the journal retain their existing synchronization, GUID resolution, active-media
deferral, exclusive verified publication and Archive barriers. These protections
still require live acceptance under concurrent native jobs before unrestricted
two-lane operation can be claimed.

## Narrow unknown-duration admission correction

Some complete MPEG inputs have video stream duration zero while the installed
wrapper reports agreeing positive container/audio durations. Under Broadcast
Continuity only, managed admission may use that container duration, with an
explicit uncertainty warning retained in QC history. This does not change global
MediaChecker or a measured positive A/V mismatch. Required video/audio,
exclusive input identity/SHA, encoder `-xerror`, complete output, full output
decode, duration guards, publication identity and Archive remain mandatory.

The existing continuity retry plan can specify `Reason:UnknownStreamDuration`
for an exact native ID/input hash currently Queued with `Input verification
failed`. Only an actual current/future use within 72 hours is eligible. Prior
errors remain in history. Done jobs, unrelated errors, hard Failed decodes and
expired-only inputs are not requeued by this addition. No journal/SQL edit or
source retransmission is required. A retry plan is not supplied by the build.

## Deployment and acceptance boundary

Use a verified application backup and online SQLite snapshot; preserve all later
GUIDs and never roll an old database over new work. Install only the server DLL
during a necessary, explicitly coordinated application maintenance window, after
active encoding, full QC and Archive finish normally. Preserve CasparCG/vMix and
the source preparation worker. Start at one lane and verify original current
slot/offset, SQLite, native identities and Archive before a bounded second lane.

Measure full native encode + QC + publication + Archive/cache completion, not
only CLI duration. Verify each result's GUID and hashes, queue priority, one
20 MiB/s source transfer, cache 200 decimal GB/72h and physical Program/NDI/vMix.
Compare transport holds against the known single-lane baseline. If signal or I/O
worsens, return to one and let both active outputs finish; do not kill encoders.

This branch alone does not authorize a maintenance interruption while the owner
has requested uninterrupted Program/Ingest/Archive observation.
