# AIR-02 readiness reconciliation and the absolute clock

The migration plugin's 15-second inventory reconciliation and 200 ms absolute
schedule timer share the WPF dispatcher. On 12 November the installed full
inventory pass correlated with status publication stalls up to 4.255 seconds.
At event 1183 the previous foreground was paused at the original boundary;
LOADBG/PLAY arrived about 3.4 seconds later, followed by automatic physical
watchdog recovery and the correct absolute offset. Later anchors did not drift.
The long repeated diagnostic ident in Empty Block 1174 was separately expected.

Reconciliation now indexes source assets, native identity suffixes and media
names once, preserving ordering, duplicate groups, case rules and GUID matching.
Its native readiness/file/cache checks still run on the original dispatcher,
in short background slices that yield to the air timer. No new encoder, worker,
database schema or cross-thread access to native event/media objects is added.
Slices defer within two seconds of an original boundary. An overlapping inventory
timer coalesces into the current pass. A failed pass retains the last complete
report and records an error; it never approves incomplete media or changes Program.
Deleted/renamed source identities are revalidated before linking a Scheduled row.

All complete-input/output, cache quota/identity, native QC, Archive and playback
gates remain in place. In particular the per-material cache readiness checks are
not replaced with a cached or less strict interpretation merely to speed the UI.
`readiness-metrics.json` records setup, slice and total durations for acceptance.

59 migration/provider/cache/override/physical-clock and boundary/identity checks
pass. A read-only benchmark on the actual 2552 source events/615 assets retained
all 1906 currently registered ordered identity pairs; the join component fell
from about 183 ms to 2.7 ms. That component benchmark is not a measurement of the
entire native reconciliation, whose repeated cache checks dominate the remainder.

Deploy only the plugin DLL after a verified application/online SQLite backup and
normal completion of active native encoding, full QC, Archive and cache publication.
Preserve CasparCG/vMix, all rundowns, original anchors and GUIDs. Verify original
current-slot recovery, physical foreground, NDI/vMix, multiple following anchors,
dispatcher age and full native preparation after the necessary application reload.
Rollback restores only the verified prior plugin DLL during a similarly drained
application window; never restore an old database over subsequently created GUIDs.

This change does not claim to repair the separately measured NDI sender/receiver
holds and does not enable the separate experimental dual-native-lane candidate.
