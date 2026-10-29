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
            if (args.Length == 5 && args[0] == "--command")
            {
                var info = BroadcastProbe.Read(args[1], args[2]);
                Console.WriteLine(BroadcastIngest.Build(info, args[2], args[3], TVideoCodec.mpeg2video,
                    TAudioCodec.pcm_s24le, (IngestColorSpace)Enum.Parse(typeof(IngestColorSpace), args[4]),
                    TFieldOrder.Unknown, TAspectConversion.NoConversion, TAudioChannelMappingConversion.FirstTwoChannels,
                    0, 60, 20, "", "00:01:00:00"));
                return 0;
            }
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
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
            Console.WriteLine("ALL " + checks + " CHECKS PASS"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
