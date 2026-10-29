# EWTN Broadcast Ready v1

The owner approved this fixed movie ingest policy on 2026-10-29. This document defines the profile; deployment/acceptance evidence is recorded separately and must include actual 1080i50 output before calling end-to-end acceptance successful.

TVPlay 1.8.9390-ewtn.2 adds the opt-in XML/config-UI `BroadcastReadyV1` directory flag. It fixes movie target HD1080i5000 on client and server, requires libx264/pcm_s24le/NoConversion/empty EncodeParams/DeleteSource=false, and rejects Copy and destination/source identity. Existing unflagged profiles retain their prior behavior. No database migration or external ingest service is used.

Output: MOV,1920x1080,25 frames/50 fields,TFF,AVC High8-bit/yuv420p,12Mbps target/max,24M VBV,GOP25/B2/closed GOP,fast preset,limited BT.709,SAR1/DAR16:9,PCM24/stereo48kHz. Rate control permits short-interval VBV bursts;12Mbps is not a promise of exact file-average bitrate on tiny clips.

Color conversion/DAR fit/pad/common audio-video timestamps remain in the native graph. Progressive50 is assembled into TFF before `scale=iw:ih:interl=1,format=yuv420p`, so chroma is downsampled per temporal field. PAL50i TFF/BFF retains its field-aware path.23.976 and exact24 separately use time-preserving repeat/drop to25,without audio speedup;25p remains25 motion instants.

Unknown source color still requires an explicit operator provenance declaration. HDR/unsupported rates remain rejected. Multichannel default first-two selection is rejected for v1 to avoid silently discarding centre dialogue;an explicit reviewed stereo selection/downmix is required. Detection is native FileSystemWatcher/refresh;operator submission uses the existing queue. All submitted v1 movies transcode;no automatic copy/remux/transcode decision engine was added. Originals are retained.

The config UI exposes the flag;setting it populates the required settings. Server validation rejects inconsistent XML settings. Select a MOV Server Media Directory. Use a separate test input folder and existing local archive for acceptance;do not ingest the entire historical library for validation.

Build:see EWTN-BUILD.md and build/Build-Managed.ps1. A public interface property changed,so rebuild/deploy the managed dependency set,configuration tool and remoting proxy together. Vendor native wrapper,FFmpeg4.1.1 and CasparCG are unchanged. GPLv2 and upstream notices apply.
