# EWTN native 1080i50 ingest changes

Experimental modifications dated 2026-10-29. Upstream: jaskie/PlayoutAutomation tag `1.8.9390`, commit `3db77d6aed76fbfe9a32c56063fd32a28a8effd2`. The original tag contains TVPlayVersion=1.8.9314 and a wildcard assembly version; the EWTN candidate identifies itself as file/assembly `1.8.9390.1`, informational `1.8.9390-ewtn.1`.

**Runtime acceptance is blocked on AIR-02.** The candidate compiled and its command/filter checks passed, but Smart App Control (`VerifiedAndReputableDesktop`) blocked a new DLL at application startup, HRESULT `0x800711C7`, CodeIntegrity events3033/3077. Original binaries/profile were restored and checked. Native ten-file ingest, rundown and NDI acceptance of this candidate have **not** run. Do not label this release Broadcast Ready. Do not disable/bypass application control or repackage files to evade the block. Obtain a supported trusted code-signing route before deployment.

## License

The repository's LICENSE is GNU GPL version2. Preserve it, upstream notices and third-party licenses. Changes are distributed under the same license. Modified/new files identify the change/date; this document explains the changes. When distributing binaries, provide the corresponding source and build instructions under the GPL, including referenced submodule revisions and required third-party source/license information. A GitHub fork alone is not proof that every binary-distribution obligation is satisfied. Private media, credentials and databases are not part of this source distribution.

## Source and project structure

Keep `upstream` pointing at `https://github.com/jaskie/PlayoutAutomation.git`. Preserve `ewtn-upstream-1.8.9390` at the baseline SHA. Develop in `ewtn/broadcast-ingest-1080i50`, with `origin` pointing at the owner's fork after it exists and the connection can access it. The upstream is not modified.

The baseline has these pinned submodules:

- dependencies/jNet.RPC: `675c21b2b5fc79c08d34a6f4f18ca1a8b1cae8ae`.
- dependencies/LibAtem: `e03f1bb9e67d93661976cd28da1e3d0f837f1008`.

Initialize the pinned public submodules without requiring a GitHub SSH key:

```powershell
git -c url.https://github.com/.insteadOf=git@github.com: submodule update --init --recursive
git submodule status
```

The local upstream push URL is disabled to guard against accidentally publishing to upstream. It does not affect fetches. Do not change an upstream tag or push this branch to jaskie/PlayoutAutomation.

`TVPlay.sln` also contains remote client, service, configuration and plugin/example projects. To build TVPlay.exe, its project-reference closure includes TAS.Client, TAS.Client.Common, TAS.Common, TAS.Server, TAS.Database.Common, SQLite/MySQL providers, localization/RPC/Svt/FTP dependencies and the referenced plugins. TAS.Server references the native C++/CLI TAS.FFMpegUtils wrapper. TVPlay.config uses TAS.Client.Config; TAS.Remoting.Proxy is compiled for the changed operation contract even though it is not required by the monolithic TVPlay.exe.

## Build on Windows without a VM/container

Normal full-source requirements: .NET SDK/WPF SDK, VS2022 MSBuild, MSVCv143 x64 and C++/CLI support, a Windows SDK, .NET Framework4.8 targeting pack/SDK, plus .NET4.5 references for the old Svt projects. NuGet restore supplies the project's declared packages. The FFmpeg headers/import libraries/binaries are already in dependencies/ffmpeg; do not substitute a newer ABI.

AIR-02 had SDK8.0.301/.NET runtime but no VC toolchain/targeting packs. Autonomous VS Build Tools installation was rejected by automatic approval review (`blocked by policy`). No Visual Studio installation was performed. A managed-only build was instead validated with official NuGet MSBuild17.10.4/Roslyn4.10.0/NuGet6.10.0/reference assemblies1.0.3 inside the source workspace, reusing the **unmodified vendor TAS.FFMpegUtils.dll**. This is not a full native-from-source build.

With the exact SDK available and a known vendor wrapper:

```powershell
# Run from the source root. This builds; it does not deploy/start the application.
powershell.exe -NoProfile -File .\build\Build-Managed.ps1 `
  -VendorNativeAssembly 'C:\Path\To\UnmodifiedVendorRelease\TAS.FFMpegUtils.dll'
```

If PowerShell execution policy prevents execution, use the organisation's approved script-signing/execution method. Do not change machine policy to run this script. The experimental session used process-scoped execution only; no permanent execution policy or PATH was changed.

The script restores pinned tools into ignored `.buildtools`, sets SDK variables only for its process, uses Framework MSBuild for WPF/resources, uses Roslyn for satellite assemblies, builds TVPlay/config/proxy and the command tests, then runs the tests. Optional `-DotnetRoot` and `-SdkVersion` must identify a deliberately selected SDK; the validated SDK is8.0.301. Most binaries are under each project's bin/x64/Release. The upstream TVPlay.config.csproj overrides OutputPath: TVPlay.config.exe and its copied dependencies are under TVPlay/bin/Release instead. This is a repeatable functional recipe, not a claim of byte-identical builds: upstream resource/tooling and build-date metadata are not fully deterministic.

An unchanged-baseline managed build completed first, with a clean source diff. Its output structure included TVPlay.exe, managed dependencies, original FFmpeg4.1.1 files, plugins, resources and the vendor wrapper. It was not started alongside the installed TVPlay.

## Native ingest implementation

- Movie target VideoFormat is selectable in the existing Ingest dialog, independently of source enum. Stream-copy cannot select a different encoding target. Existing source-format defaults are retained; asynchronous verification cannot overwrite an operator's explicit selection.
- A target of HD1080i5000 with encoded video invokes the in-process BroadcastIngest command builder through the existing IngestOperation/FileManager/FFMpegHelper queue. Other targets retain the upstream workflow. No external ingest service or database schema was added.
- ffprobe4.1.1 reads actual rate, raster, SAR/DAR, field order, range and color metadata. Unknown field order is checked on decoded frames; it is not inferred from the filename. The unchanged C++/CLI metadata wrapper need not expose new color properties.
- Progressive23.976 and24.000 are separate inputs. Both use time-preserving repeat/drop to25fps, then50 samples→TFF interlace. There is no PAL speedup or audio time stretch. Judder remains. 25p naturally has25 moments of movement/PsF. 50p goes directly through50fps→interlace, never25p. PAL50i preserves field timing with fieldorder=tff and scale interl=1.
- Actual source DAR determines aspect-preserving fit/pad with dimensions aligned for interlaced4:2:2. 2048x858 becomes1920x804+138px top/bottom; PAL4:3 becomes1440x1080+240px left/right. NoConversion means fit/pad for this broadcast target; legacy crop/PillarBox/LetterBox presets are rejected rather than stretched.
- Auto color requires known supported SDR tags/range. Unknown tags fail with an actionable message. Bt709/Bt601625 are explicit operator declarations after checking provenance; they are logged. Full/limited range is retained through scaling and converted once by colorspace to limited709. HDR/2020, RGB and unsupported interlaced rates are rejected. A color tag alone is not used as a color transform.
- One complex graph maps video and audio explicitly. Both subtract the **same format.start_time**, trim the same source interval and subtract the same trim start; neither is independently reset to STARTPTS. This avoids the prior leading-B-frame analysis error. No audio offset compensation is inserted.
- Stereo48kHz is encoded, mono is duplicated and missing audio gets bounded silence. Stereo selections from the first audio stream are supported; multi-stream MergeAllChannels and absent channel selections are rejected. Existing volume correction is honored. Copy audio is rejected for this normalized target.
- MPEG2/yuv422p/50M/GOP12/B2 with PCM24/MOV is the tested experimental command profile. libx264 is accepted by the generator but has not received native runtime acceptance. NVENC interlacing is rejected. Preserve a10-bit original/master: MPEG2 4:2:2 output is8-bit.
- EncodeParams permits only codec rate/GOP/thread numeric options; structural graph/timing/color/map overrides are rejected. DeleteSource=true is rejected for broadcast ingest. Existing local inputs/originals are not modified.

Changed components: TAS.Common operation contract/enum, TAS.Client dialog/view model, TAS.Server builder/probe/native operation, TAS.Remoting.Proxy contract, and assembly identity. Other managed projects are rebuilt to keep references consistent. CasparCG and installed FFmpeg are unchanged.

## Checks already completed

The console `tests/Ingest.Tests` suite checks film rates separately,50p cadence, TFF/BFF preservation path,SD/cinema fit, common timestamps, stereo/mono/silence, explicit color declarations and rejection of HDR/filter collisions/unsupported encoders/audio copy. All25 initial checks passed. Commands were generated from all ten real control files and FFmpeg decoded a short segment to the null muxer10/10. Those checks do not create broadcast media and do not substitute for native Ingest acceptance.

## Deployment and rollback

1. Work only on AIR-02. Establish the supported code-signing/trust path first. Do not change production, SDI, Windows protection, CasparCG/vMix or FFmpeg versions.
2. Confirm no playing/fading rundown events and no FFmpeg jobs. Make an ACL-protected online SQLite backup, app/config backup and file hashes. Record all rundown rows and schema (SQLite user_version2).
3. Close TVPlay normally and confirm process exit; take another closed DB copy. Do not copy over running executables.
4. Replace only explicitly manifested managed executables/DLLs and existing enabled/repository plugins/resources. Preserve ffmpeg/ffprobe/FFMpegUtils, third-party native DLLs, TVPlay.exe.config, existing Configuration, media roots, DB and enabled plugin set. Create a separate EWTN test profile with DeleteSource=false, MPEG2/PCM24, no structural EncodeParams.
5. Launch exactly one TVPlay. If startup fails, close the failure dialog/process, restore manifested original files and the previous profile. Check original hashes, DB integrity/schema/rundown and dependent process IDs before further action. Do not retry by renaming/repacking files to evade App Control.
6. The actual attempted deployment rolled back successfully:28/28 original hashes,47 rundown rows,14 schema tables,integrity ok; CasparCG/vMix PIDs preserved. No schema migration occurred.
7. A future successful launch must ingest all ten20s controls through the UI, then clock/color patterns; verify full decode,1920x1080,SAR1,DAR16:9,25fps,TFF,709/limited,PCM48k stereo, field chronology, A/V timing and library registration. Add a separate manual rundown through UI and verify Caspar/NDI playback. Existing channels1080p25 do not prove end-to-end1080i50 output. Keep originals unchanged.

For rollback after successful new test registrations, restoring only binaries/profile preserves those DB entries; restore a DB backup only if needed, while TVPlay is closed, after preserving the failed/new DB privately. Rollback may intentionally lose only the newly created lab entries, never original media. Backups and prepared media remain outside Git.
