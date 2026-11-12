// EWTN modified 2026-10-29: native 1080i50 ingest. GPL-2.0; see LICENSE.
using System;
using System.Globalization;
using TAS.Common;
using TAS.Server.MediaOperation;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidOperationException) { checks++; Console.WriteLine("PASS reject " + name); return; }
        throw new Exception("Did not reject " + name);
    }
    private static BroadcastProbe Input(string rate = "25/1", string fields = "progressive", int width = 1920, int height = 1080, string dar = "16:9")
    {
        return new BroadcastProbe
        {
            Format = new BroadcastFormat { StartTime = "0.685389", Duration = "100" },
            Streams = new[] {
                new BroadcastStream { Index=0, Type="video", Width=width, Height=height, Rate=rate, AverageRate=rate,
                    FieldOrder=fields, Sar="1:1", Dar=dar, PixelFormat="yuv420p", Primaries="bt709", Transfer="bt709", Matrix="bt709", Range="tv" },
                new BroadcastStream { Index=1, Type="audio", Channels=2 }
            }
        };
    }
    private static string Build(BroadcastProbe source, IngestColorSpace color = IngestColorSpace.Auto,
        string extra = "", TVideoCodec codec = TVideoCodec.mpeg2video, TAudioCodec audio = TAudioCodec.pcm_s24le)
    {
        return BroadcastIngest.Build(source, "D:\\test source.mov", "D:\\test result.mov", codec, audio, color,
            TFieldOrder.Unknown, TAspectConversion.NoConversion, TAudioChannelMappingConversion.FirstTwoChannels, 0, 60, 20, extra, "00:01:00:00");
    }
    private static int Main(string[] args)
    {
        try
        {
            Check(NativeIngestPolicy.Limit(1,"AIR-02","AIR-02",2,true)==2,"owner host policy allows two native lanes");
            Check(NativeIngestPolicy.Limit(1,"AIR-02","OTHER",2,true)==1,"native lane policy cannot apply on other host");
            Check(NativeIngestPolicy.Limit(1,"AIR-02","AIR-02",3,true)==1,"more than two native lanes fails closed");
            Check(NativeIngestPolicy.Limit(1,"AIR-02","AIR-02",0,true)==1,"zero native lanes is not an admission pause");
            Check(NativeIngestPolicy.Limit(1,"AIR-02","AIR-02",2,false)==1,"two lanes require active continuity mode");
            var laneId=Guid.NewGuid();var lanes=new NativeIngestPolicy.Settings{MaxParallelJobs=2,SecondLaneJobIds=new[]{laneId.ToString()}};
            Check(NativeIngestPolicy.CanStart(lanes,laneId,1,1,false),"bounded native second lane accepts explicit test identity");
            Check(!NativeIngestPolicy.CanStart(lanes,Guid.NewGuid(),1,1,false),"bounded native second lane refuses unlisted identity");
            Check(!NativeIngestPolicy.CanStart(lanes,laneId,2,2,false),"native admission never starts a third job");
            Check(!NativeIngestPolicy.CanStart(lanes,laneId,0,0,true),"same-name unfinished revision serializes before encoding");
            Check(NativeIngestPolicy.CanStart(lanes,Guid.NewGuid(),0,0,false),"primary lane retains nearest-air admission");
            Check(NativeIngestPolicy.Precedes(100,Guid.Parse("00000000-0000-0000-0000-000000000002"),100,Guid.Parse("00000000-0000-0000-0000-000000000001")),"equal-created same-name jobs have deterministic predecessor");
            Check(!NativeIngestPolicy.Precedes(100,Guid.Parse("00000000-0000-0000-0000-000000000001"),100,Guid.Parse("00000000-0000-0000-0000-000000000002")),"same-name predecessor rule cannot mutually deadlock equal-created jobs");
            Check(NativeIngestPolicy.ContainerDurationFallback(true,true,true,0,18.168,18.168),"unknown video duration uses agreeing complete container/audio metadata");
            Check(!NativeIngestPolicy.ContainerDurationFallback(true,true,true,18,20,20),"measured A/V duration mismatch is not softened");
            Check(!NativeIngestPolicy.ContainerDurationFallback(true,true,false,0,18,18),"missing video is never a duration fallback");
            Check(!NativeIngestPolicy.ContainerDurationFallback(true,true,true,0,0,18),"missing audio is never a duration fallback");
            Check(!NativeIngestPolicy.ContainerDurationFallback(true,false,true,0,18,18),"duration fallback does not change Library/Archive or legacy QC");
            Check(!NativeIngestPolicy.ContainerDurationFallback(false,true,true,0,18,18),"Strict metadata behavior is unchanged");
            Check(!NativeIngestPolicy.ContainerDurationFallback(true,true,true,0,18,20),"unknown video plus conflicting audio/container remains blocked");
            Check(!NativeIngestPolicy.ContainerDurationFallback(true,true,true,double.NaN,18,18),"invalid metadata is not assumed safe");
            Check(NativeIngestPolicy.WakeQueued("Queued","Input verification failed","UnknownStreamDuration"),"metadata retry intent wakes retained queued input without changing state");
            Check(!NativeIngestPolicy.WakeQueued("Failed","decode failed","UnknownStreamDuration"),"metadata retry intent cannot wake a hard Failed decode");
            Check(!NativeIngestPolicy.WakeQueued("Done","Input verification failed","UnknownStreamDuration"),"completed native input is never repeated by admission retry");
            Check(!NativeIngestPolicy.WakeQueued("Queued","different error","UnknownStreamDuration"),"metadata retry refuses unrelated queued error evidence");
            if (args.Length == 5 && (args[0] == "--command" || args[0] == "--command-v1"))
            {
                var info = BroadcastProbe.Read(args[1], args[2]);
                var v1 = args[0] == "--command-v1";
                Console.WriteLine(BroadcastIngest.Build(info, args[2], args[3], v1 ? TVideoCodec.libx264 : TVideoCodec.mpeg2video,
                    TAudioCodec.pcm_s24le, (IngestColorSpace)Enum.Parse(typeof(IngestColorSpace), args[4]),
                    TFieldOrder.Unknown, TAspectConversion.NoConversion, TAudioChannelMappingConversion.FirstTwoChannels,
                    0, 60, 20, "", "00:01:00:00", v1));
                return 0;
            }
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
            var interlacedProducer = BroadcastPlayout.Producer("\"clip\"", true, true, TVideoFormat.HD1080i5000, TVideoFormat.HD1080i5000);
            Check(interlacedProducer == "\"clip\" FILTER \"scale=iw:ih:interl=1,format=yuv422p\"", "1080i TVP producer expands chroma by field");
            Check(BroadcastPlayout.Producer("\"clip\"", false, true, TVideoFormat.HD1080i5000, TVideoFormat.HD1080i5000) == "\"clip\"", "other server producer unchanged");
            Check(BroadcastPlayout.Producer("\"clip\"", true, true, TVideoFormat.HD1080i5000, TVideoFormat.HD1080p2500) == "\"clip\"", "progressive output producer unchanged");
            Check(BroadcastPlayout.Producer("\"clip\"", true, false, TVideoFormat.HD1080i5000, TVideoFormat.HD1080i5000) == "\"clip\"", "still producer unchanged");
            Check(BroadcastPlayout.Producer("\"clip\"", true, true, TVideoFormat.HD1080p2500, TVideoFormat.HD1080i5000) == "\"clip\"", "progressive source producer unchanged");
            foreach (var rate in new[] { "24000/1001", "24/1", "25/1" })
            {
                var command = Build(Input(rate));
                Check(command.Contains("fps=25:start_time=0,fps=50:start_time=0"), rate + " preserves time using repeats");
                Check(command.Contains("interlace=scan=tff") && !command.Contains("atempo") && !command.Contains("PTS*"), rate + " no speedup");
            }
            var p50 = Build(Input("50/1"));
            Check(p50.Contains("fps=50:start_time=0") && !p50.Contains("fps=25"), "50p never passes through25p");
            Check(p50.Contains("interlace=scan=tff"), "50p combines different temporal frames into fields");
            foreach (var order in new[] { "tt", "bb" })
            {
                var command = Build(Input("25/1", order));
                Check(command.Contains("fieldorder=tff") && command.Contains("interl=1") && !command.Contains("interlace=scan"), order + " keeps original field samples");
            }
            Check(Build(Input("25/1", "bb", 720, 576, "4:3")).Contains("scale=1440:1080:interl=1") &&
                Build(Input("25/1", "bb", 720, 576, "4:3")).Contains("pad=1920:1080:240:0"), "SD4:3 pillarbox and field scaling");
            Check(Build(Input("25/1", "progressive", 2048, 858, "1024:429")).Contains("pad=1920:1080:0:138"), "cinema fit/pad");
            var baseCommand = Build(Input());
            Check(baseCommand.Contains("setpts=PTS-0.685389/TB") && baseCommand.Contains("asetpts=PTS-0.685389/TB") && !baseCommand.Contains("STARTPTS"), "common video/audio origin and invariant decimals");
            Check(baseCommand.Contains("trim=start=60:duration=20") && baseCommand.Contains("atrim=start=60:duration=20"), "same trim window");
            Check(baseCommand.Contains("-c:a pcm_s24le -ar 48000 -ac 2"), "PCM24 stereo48k");
            var unknown = Input(); unknown.Streams[0].Primaries = null;
            Reject(() => Build(unknown), "untagged source is not silently guessed");
            Check(Build(unknown, IngestColorSpace.Bt709).Contains("colorspace=iall=bt709"), "explicit709 declaration");
            Check(Build(unknown, IngestColorSpace.Bt601625).Contains("colorspace=iall=bt601-6-625:all=bt709"), "actual601 to709 transform");
            var hdr = Input(); hdr.Streams[0].Transfer = "smpte2084";
            Reject(() => Build(hdr, IngestColorSpace.Bt709), "HDR even with override");
            Reject(() => Build(Input(), extra:"-vf fps=25"), "extra filter graph collision");
            Reject(() => Build(Input(), extra:"-r:v 25"), "extra cadence override");
            Reject(() => Build(Input(), codec:TVideoCodec.h264_nvenc), "unsupported interlaced encoder");
            Reject(() => Build(Input(), audio:TAudioCodec.copy), "audio copy cannot guarantee48k");
            var mono = Input(); mono.Streams[1].Channels = 1;
            Check(Build(mono).Contains("pan=stereo|c0=c0|c1=c0"), "mono dual-channel");
            var silent = Input(); silent.Streams = new[] { silent.Streams[0] };
            Check(Build(silent).Contains("anullsrc=channel_layout=stereo:sample_rate=48000") && Build(silent).Contains("-shortest"), "silent video gets bounded stereo silence");
            Func<BroadcastProbe, string, TVideoCodec, TAudioCodec, string> ready = (probe, extra, codec, audio) =>
                BroadcastIngest.Build(probe, "D:\\input.mov", "D:\\output.mov", codec, audio, IngestColorSpace.Auto,
                    TFieldOrder.Unknown, TAspectConversion.NoConversion, TAudioChannelMappingConversion.FirstTwoChannels,
                    0, 60, 20, extra, "00:01:00:00", true);
            foreach (var rate in new[] { "24000/1001", "24/1", "25/1", "50/1" })
            {
                var command = ready(Input(rate), "", TVideoCodec.libx264, TAudioCodec.pcm_s24le);
                Check(command.Contains("-profile:v high") && command.Contains("-b:v 12M -maxrate 12M -bufsize 24M") && command.Contains("-pix_fmt yuv420p"), rate + " fixed Ready v1 AVC420 High12M");
                Check(command.IndexOf("interlace=scan=tff", StringComparison.Ordinal) < command.IndexOf("scale=iw:ih:interl=1,format=yuv420p", StringComparison.Ordinal), rate + " field-aware chroma AFTER temporal interlace");
            }
            foreach (var fields in new[] { "tt", "bb" })
            {
                var command = ready(Input("25/1", fields), "", TVideoCodec.libx264, TAudioCodec.pcm_s24le);
                Check(command.Contains("fieldorder=tff") && command.Contains("scale=iw:ih:interl=1,format=yuv420p"), fields + " interlaced chroma path");
            }
            Reject(() => ready(Input(), "-b:v 8M", TVideoCodec.libx264, TAudioCodec.pcm_s24le), "Ready v1 bitrate override");
            Reject(() => ready(Input(), "", TVideoCodec.copy, TAudioCodec.pcm_s24le), "Ready v1 copy");
            Reject(() => ready(Input(), "", TVideoCodec.libx264, TAudioCodec.aac), "Ready v1 non-PCM24");
            var surround = Input(); surround.Streams[1].Channels = 6;
            Reject(() => ready(surround, "", TVideoCodec.libx264, TAudioCodec.pcm_s24le), "Ready v1 multichannel default selection");
            Func<BroadcastProbe,System.Collections.Generic.List<string>,string> continuity=(probe,emitted)=>
                BroadcastIngest.Build(probe,"D:\\in.mov","D:\\out.mov",TVideoCodec.libx264,TAudioCodec.pcm_s24le,IngestColorSpace.Auto,
                    TFieldOrder.Unknown,TAspectConversion.NoConversion,TAudioChannelMappingConversion.FirstTwoChannels,0,0,null,"","00:00:00:00",true,true,emitted.Add);
            var warnings=new System.Collections.Generic.List<string>();
            var uncertain=Input();uncertain.Streams[0].Range=null;uncertain.Streams[0].Primaries=null;uncertain.Streams[0].Matrix=null;uncertain.Streams[0].Transfer=null;
            var fallback=continuity(uncertain,warnings);
            Check(fallback.Contains("colorspace=iall=bt709")&&fallback.Contains("in_range=tv")&&warnings.Count==2,"continuity HD709/limited assumptions are explicitly warned");
            uncertain.Streams[0].Height=576;uncertain.Streams[0].Width=720;
            Check(continuity(uncertain,warnings).Contains("iall=bt601-6-625"),"continuity SD576 uses documented625 fallback");
            uncertain.Streams[0].Height=480;
            Check(continuity(uncertain,warnings).Contains("iall=bt601-6-525"),"continuity SD480 uses documented525 fallback");
            uncertain.Streams[0].PixelFormat="yuvj420p";
            Check(continuity(uncertain,warnings).Contains("in_range=pc"),"explicit JPEG pixel family selects full range fallback");
            uncertain.Streams[0].Range="pc";
            Check(continuity(uncertain,warnings).Contains("irange=pc"),"known full range is retained");
            Reject(()=>continuity(hdr,warnings),"continuity never guesses HDR as SDR");
            var wide=Input();wide.Streams[0].Matrix="bt2020nc";Reject(()=>continuity(wide,warnings),"explicit wide-gamut matrix remains blocking");
            var unknownTransfer=Input();unknownTransfer.Streams[0].Transfer="smpte428";Reject(()=>continuity(unknownTransfer,warnings),"unapproved cinema transfer remains blocking");
            Check(continuity(Input("30000/1001","tt"),warnings).Contains("yadif=mode=send_field")&&continuity(Input("30000/1001","tt"),warnings).Contains("interlace=scan=tff"),"non25 interlace is field decoded and reassembled50i");
            Check(continuity(Input("0/0"),warnings).Contains("fps=50")&&!continuity(Input("0/0"),warnings).Contains("fps=25"),"unknown nominal rate uses decoded cadence without25p drop");
            Reject(()=>continuity(silent,warnings),"continuity missing required audio is blocking");
            Reject(()=>ready(silent,"",TVideoCodec.libx264,TAudioCodec.pcm_s24le),"Strict Ready v1 missing audio is also blocking");
            Reject(()=>continuity(surround,warnings),"unknown multichannel layout remains blocking");
            surround.Streams[1].ChannelLayout="5.1";
            Check(continuity(surround,warnings).Contains("aformat=channel_layouts=stereo"),"declared surround uses centre-preserving standard downmix");
            Check(BroadcastContinuityQc.HostAllows("BroadcastContinuity","AIR-02","AIR-02")&&!BroadcastContinuityQc.HostAllows("BroadcastContinuity","AIR-02","PRODUCTION"),"policy is restricted to experimental AIR-02");
            Check(!BroadcastContinuityQc.HostAllows("Strict","AIR-02","AIR-02"),"Strict rollback disables all continuity assumptions");
            Check(BroadcastContinuityQc.DurationAllowed(1800,1801,true)&&!BroadcastContinuityQc.DurationAllowed(1800,1801,false),"small fully decoded overrun can warn only in continuity");
            Check(!BroadcastContinuityQc.DurationAllowed(1800,1799,true)&&!BroadcastContinuityQc.DurationAllowed(1800,1803,true),"short output and large overrun remain blocking");
            Check(!BroadcastContinuityQc.DurationAllowed(0,1,true)&&!BroadcastContinuityQc.DurationAllowed(10,double.NaN,true),"unknown or nonfinite duration remains blocking");
            Check(BroadcastContinuityQc.SoftFailure("Unknown source range: declare the input color interpretation explicitly.")&&!BroadcastContinuityQc.SoftFailure("Unknown source range; decode failed"),"retry allows soft criterion and rejects combined technical failure");
            Console.WriteLine("ALL " + checks + " CHECKS PASS"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
