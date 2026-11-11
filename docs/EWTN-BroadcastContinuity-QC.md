# Experimental AIR-02: Broadcast Continuity QC

Explicit owner authorization, 2026-11-11. This policy applies only to new managed
Incoming BRv1 operations on machine AIR-02. The default remains Strict elsewhere.
No production VPlay data is changed.

`%ProgramData%\EWTN\Playout\Migration\broadcast-continuity-qc.json`:

```json
{"Version":1,"Host":"AIR-02","Mode":"BroadcastContinuity"}
```

Missing, invalid, wrong-host or `Mode: Strict` configuration selects Strict.
Replace the file atomically when changing mode. A running native operation retains
the policy it captured at start. Strict rollback affects subsequent operations;
it does not delete previously prepared media, GUIDs or warning history.

The output is unchanged: MOV, H.264 High8-bit420/12M, 1920x1080 TFF50 fields/s,
BT.709 limited, PCM24 stereo48k. Full output decode and publication SHA remain.

Continuity warnings and conversions:

- Missing/ambiguous recognized SDR tags: assume BT.709 for HD (height>=720 or
  width>=1280), BT.601625 for remaining SD above480 lines, BT.601525 at<=480 lines.
  Unknown YUV range assumes limited, except explicit yuvj pixel families assume full.
  Known full/limited range is respected. Each assumption is logged as uncertain;
  inferred source metadata is never presented as established provenance.
- Non25fps interlaced input: yadif send_field, decoded timestamp conversion to50p,
  then TFF interlace. Known25fps interlace keeps the existing field-preserving path.
  Unknown nominal rate uses decoded timestamps and50p, without an intermediate25p.
- A declared multichannel layout uses standard FFmpeg stereo downmix. Unknown layout
  remains blocked; centre dialogue is not silently discarded as first-two channels.
- Nonzero source timecode is retained in source metadata, while untrimmed managed
  output address is normalized to zero. Actual content timing is not rebased.
- Loudness deviations are advisory; the existing gain remains, with no compliance
  claim. No new automatic loudness gain is introduced.
- Fully decoded positive duration overrun may warn up to min(2s,max(.5s,.1% source)).
  The existing half-second shortfall guard is retained; larger short output, unknown
  duration and larger overruns still fail. Full decode is not bypassed by tolerance.

HDR/BT.2020, unapproved explicit non-SDR transfer, missing required video/audio,
container/decode errors, incomplete source delivery, invalid output and integrity
failures remain blocking. Continuity does not synthesize missing required audio.

The native job records QcMode and QcWarnings. An exact job-ID/input-SHA retry plan
may request a retry of a failed soft criterion only. The prior error/attempt count
is retained in QcHistory, with the native ID and Target GUID unchanged. Retained
input identity/hash is checked again; no network redelivery is needed.

Durable ContinuityRetryPending intents stay in Failed until a real encoder lane
opens. They do not reserve all healthy ahead slots. Native admission recomputes
the nearest current/future use from the existing events/catalog in Kyiv time,
within72h; expired-only work does not encode. Repeated assets use their next slot.
New queued inputs and retry intents share that air-time ordering. Existing encoding
finishes normally. Ready publication precedes new admission; slow archive cleanup
does not take the admission turn before an already delivered nearer input.

The normal preparation worker retains one network transfer at20MiB/s. C-cache is
still200decimalGB/72h; D/E Library and Archive are persistent. Editorial schedule
review, trims, audio matrices and live-event semantics are separate from input QC.
This policy does not silently reinterpret them or shift original schedule dates.
