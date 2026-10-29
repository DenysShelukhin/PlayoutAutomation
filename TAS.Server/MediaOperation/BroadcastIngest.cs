// EWTN modifications, 2026-10-29. GPL-2.0; see repository LICENSE.
// Generates a single native-ingest graph. Does not execute an external ingest workflow.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using TAS.Common;

namespace TAS.Server.MediaOperation
{
    [DataContract]
    internal sealed class BroadcastStream
    {
        [DataMember(Name = "index")] public int Index;
        [DataMember(Name = "codec_type")] public string Type;
        [DataMember(Name = "width")] public int Width;
        [DataMember(Name = "height")] public int Height;
        [DataMember(Name = "channels")] public int Channels;
        [DataMember(Name = "sample_aspect_ratio")] public string Sar;
        [DataMember(Name = "display_aspect_ratio")] public string Dar;
        [DataMember(Name = "r_frame_rate")] public string Rate;
        [DataMember(Name = "avg_frame_rate")] public string AverageRate;
        [DataMember(Name = "field_order")] public string FieldOrder;
        [DataMember(Name = "pix_fmt")] public string PixelFormat;
        [DataMember(Name = "color_primaries")] public string Primaries;
        [DataMember(Name = "color_transfer")] public string Transfer;
        [DataMember(Name = "color_space")] public string Matrix;
        [DataMember(Name = "color_range")] public string Range;
    }

    [DataContract]
    internal sealed class BroadcastFormat
    {
        [DataMember(Name = "start_time")] public string StartTime;
        [DataMember(Name = "duration")] public string Duration;
    }

    [DataContract]
    internal sealed class BroadcastFrame
    {
        [DataMember(Name = "interlaced_frame")] public int Interlaced;
        [DataMember(Name = "top_field_first")] public int TopFirst;
    }

    [DataContract]
    internal sealed class BroadcastProbe
    {
        [DataMember(Name = "streams")] public BroadcastStream[] Streams;
        [DataMember(Name = "format")] public BroadcastFormat Format;
        [DataMember(Name = "frames")] public BroadcastFrame[] Frames;

        internal static BroadcastProbe Read(string executable, string source)
        {
            var result = Parse(Run(executable, "-v error -show_streams -show_format -of json " + Quote(source)));
            var video = result.Streams?.FirstOrDefault(s => s.Type == "video");
            if (video == null) throw new InvalidOperationException("Broadcast ingest requires a video stream.");
            if (string.IsNullOrEmpty(video.FieldOrder) || video.FieldOrder == "unknown")
            {
                var first = Parse(Run(executable, "-v error -select_streams v:0 -read_intervals %+#12 -show_entries frame=interlaced_frame,top_field_first -of json " + Quote(source)));
                var frame = first.Frames?.FirstOrDefault();
                if (frame == null) throw new InvalidOperationException("Could not decode source field structure.");
                video.FieldOrder = frame.Interlaced == 0 ? "progressive" : (frame.TopFirst != 0 ? "tt" : "bb");
            }
            return result;
        }

        internal static BroadcastProbe Parse(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (BroadcastProbe)new DataContractJsonSerializer(typeof(BroadcastProbe)).ReadObject(stream);
        }

        private static string Run(string executable, string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo(executable, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }))
            {
                if (process == null) throw new InvalidOperationException("Could not start ffprobe.");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new InvalidOperationException("ffprobe timed out after 30 seconds.");
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("ffprobe failed: " + error.GetAwaiter().GetResult());
                return output.GetAwaiter().GetResult();
            }
        }

        internal static string Quote(string path)
        {
            if (path == null || path.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
                throw new ArgumentException("Invalid media path.");
            return "\"" + path + "\"";
        }
    }

    internal static class BroadcastIngest
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly HashSet<string> ExtraOptions = new HashSet<string>(StringComparer.Ordinal)
        { "-b:v", "-minrate", "-maxrate", "-bufsize", "-g", "-bf", "-dc", "-threads", "-b:a", "-q:v" };

        internal static double Number(string value, double fallback = 0)
        {
            return double.TryParse(value, NumberStyles.Float, Inv, out var n) && !double.IsNaN(n) && !double.IsInfinity(n) ? n : fallback;
        }

        internal static double Ratio(string value, double fallback = 0)
        {
            var parts = (value ?? "").Split('/', ':');
            return parts.Length == 2 && Number(parts[1]) > 0 ? Number(parts[0]) / Number(parts[1]) : fallback;
        }

        private static string N(double number) => number.ToString("0.#########", Inv);

        private static string AdditionalParameters(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var words = Regex.Split(value.Trim(), @"\s+");
            if (words.Length % 2 != 0) throw new InvalidOperationException("1080i50 EncodeParams must contain option/value pairs.");
            for (var i = 0; i < words.Length; i += 2)
                if (!ExtraOptions.Contains(words[i]) || !Regex.IsMatch(words[i + 1], @"^[0-9A-Za-z./:+_-]+$"))
                    throw new InvalidOperationException("1080i50 EncodeParams cannot override filters, timing, geometry, color or stream maps: " + words[i]);
            return " " + string.Join(" ", words);
        }

        internal static string Build(BroadcastProbe info, string source, string destination, TVideoCodec videoCodec,
            TAudioCodec audioCodec, IngestColorSpace inputColor, TFieldOrder fieldOverride,
            TAspectConversion aspect, TAudioChannelMappingConversion mapping, double volume,
            double start, double? duration, string additional, string timecode)
        {
            if (videoCodec != TVideoCodec.mpeg2video && videoCodec != TVideoCodec.libx264)
                throw new InvalidOperationException("1080i50 requires an interlace-capable encoder: mpeg2video or libx264.");
            if (audioCodec == TAudioCodec.copy)
                throw new InvalidOperationException("1080i50 audio must be encoded to stereo 48 kHz, not stream-copied.");
            if (start < 0 || duration.HasValue && (duration.Value <= 0 || double.IsNaN(duration.Value) || double.IsInfinity(duration.Value)) || double.IsNaN(start) || double.IsInfinity(start))
                throw new InvalidOperationException("Invalid broadcast trim region.");
            var v = info.Streams.First(s => s.Type == "video");
            if (v.Width <= 0 || v.Height <= 0 || v.PixelFormat == null || !v.PixelFormat.StartsWith("yuv", StringComparison.Ordinal))
                throw new InvalidOperationException("This broadcast profile requires a decoded YUV SDR source.");
            if (v.Primaries == "bt2020" || v.Transfer == "smpte2084" || v.Transfer == "arib-std-b67")
                throw new InvalidOperationException("HDR/BT.2020 requires a separately approved conversion; do not relabel as BT.709.");
            var range = v.Range;
            if (range != "tv" && range != "pc")
            {
                if (inputColor == IngestColorSpace.Auto) throw new InvalidOperationException("Unknown source range: declare the input color interpretation explicitly.");
                range = "tv"; // Explicit operator declaration includes limited range if source range is absent.
            }
            string color;
            if (inputColor == IngestColorSpace.Bt709) color = "iall=bt709";
            else if (inputColor == IngestColorSpace.Bt601625) color = "iall=bt601-6-625";
            else if (v.Primaries == "bt709" && v.Transfer == "bt709" && v.Matrix == "bt709") color = "iall=bt709";
            else if (v.Primaries == "bt470bg" && v.Transfer == "bt709" && v.Matrix == "bt470bg")
                color = "iprimaries=bt470bg:itrc=bt709:ispace=bt470bg";
            else throw new InvalidOperationException("Unknown/unsupported SDR source color tags: declare BT.709 or BT.601-625 after checking provenance.");

            var rate = Ratio(v.AverageRate, Ratio(v.Rate));
            if (rate <= 0) throw new InvalidOperationException("Source frame rate is unknown.");
            var interlaced = v.FieldOrder == "tt" || v.FieldOrder == "bb" || v.FieldOrder == "tb" || v.FieldOrder == "bt";
            if (fieldOverride != TFieldOrder.Unknown) interlaced = fieldOverride != TFieldOrder.Progressive;
            if (interlaced && Math.Abs(rate - 25) > 0.02)
                throw new InvalidOperationException("Interlaced sources in this profile must have 25 frames / 50 fields per second.");
            var sourceDar = Ratio(v.Dar, v.Width * Ratio(v.Sar, 1) / v.Height);
            if (aspect == TAspectConversion.Force4_3) sourceDar = 4.0 / 3;
            else if (aspect == TAspectConversion.Force16_9) sourceDar = 16.0 / 9;
            else if (aspect != TAspectConversion.NoConversion)
                throw new InvalidOperationException("1080i50 uses DAR-aware fit/pad; select NoConversion or an explicit source DAR declaration.");
            if (sourceDar <= 0 || double.IsNaN(sourceDar) || double.IsInfinity(sourceDar)) throw new InvalidOperationException("Invalid source DAR.");
            var width = 1920;
            var height = 1080;
            if (sourceDar > 16.0 / 9) height = Math.Max(4, (int)Math.Floor(1920 / sourceDar / 4) * 4);
            else width = Math.Max(4, (int)Math.Floor(1080 * sourceDar / 4) * 4);
            var origin = Number(info.Format?.StartTime);
            var vf = new List<string> { "setpts=PTS-" + N(origin) + "/TB" };
            // Trim video/audio on one original timeline BEFORE changing cadence. No independent STARTPTS reset.
            if (start > 0 || duration.HasValue)
                vf.Add("trim=start=" + N(start) + (duration.HasValue ? ":duration=" + N(duration.Value) : ""));
            vf.Add("setpts=PTS-" + N(start) + "/TB");
            if (fieldOverride != TFieldOrder.Unknown)
                vf.Add("setfield=" + (fieldOverride == TFieldOrder.TFF ? "tff" : fieldOverride == TFieldOrder.BFF ? "bff" : "prog"));
            if (interlaced) vf.AddRange(new[] { "fieldorder=tff", "fps=25:start_time=0" });
            else
            {
                if (rate <= 25.02) vf.Add("fps=25:start_time=0");
                vf.Add("fps=50:start_time=0");
            }
            vf.Add("scale=" + width + ":" + height + ":interl=" + (interlaced ? "1" : "0") + ":in_range=" + range + ":out_range=" + range + ":flags=lanczos+accurate_rnd");
            vf.Add("format=yuv444p");
            vf.Add("colorspace=" + color + ":all=bt709:irange=" + range + ":range=tv:format=yuv422p");
            vf.Add("setsar=1");
            if (width != 1920 || height != 1080) vf.Add("pad=1920:1080:" + (1920 - width) / 2 + ":" + (1080 - height) / 2 + ":color=black");
            if (!interlaced) vf.Add("interlace=scan=tff");
            vf.Add("setfield=tff");
            if (duration.HasValue) vf.Add("trim=duration=" + N(duration.Value));
            var graph = "[0:" + v.Index + "]" + string.Join(",", vf) + "[v];";
            var audio = info.Streams.FirstOrDefault(s => s.Type == "audio");
            if (audio == null)
            {
                var length = duration ?? Number(info.Format?.Duration);
                if (length <= 0) throw new InvalidOperationException("Silent source duration is unknown.");
                graph += "anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration=" + N(length) + "[a]";
            }
            else
            {
                if (audio.Channels <= 0) throw new InvalidOperationException("Audio channel count is unknown.");
                if (mapping == TAudioChannelMappingConversion.MergeAllChannels)
                    throw new InvalidOperationException("1080i50 requires a stereo channel selection, not MergeAllChannels.");
                var pan = MediaConversion.AudioChannelMapingConversions[mapping].FFMpegFilter;
                if (mapping == TAudioChannelMappingConversion.Default || mapping == TAudioChannelMappingConversion.FirstTwoChannels)
                    pan = audio.Channels == 1 ? "pan=stereo|c0=c0|c1=c0" : "pan=stereo|c0=c0|c1=c1";
                if (string.IsNullOrWhiteSpace(pan)) throw new InvalidOperationException("Unsupported stereo mapping.");
                var required = Regex.Matches(pan, @"c(\d+)").Cast<Match>().Max(m => int.Parse(m.Groups[1].Value, Inv));
                if (required >= audio.Channels && !(audio.Channels == 1 && pan.EndsWith("c1=c0", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Requested audio channels are absent from the first audio stream.");
                graph += "[0:" + audio.Index + "]asetpts=PTS-" + N(origin) + "/TB";
                if (start > 0 || duration.HasValue) graph += ",atrim=start=" + N(start) + (duration.HasValue ? ":duration=" + N(duration.Value) : "");
                graph += ",asetpts=PTS-" + N(start) + "/TB,aresample=48000," + pan;
                if (Math.Abs(volume) > double.Epsilon) graph += ",volume=" + N(volume) + "dB";
                graph += "[a]";
            }
            var ep = new StringBuilder(" -copyts -filter_complex_threads 2 -i " + BroadcastProbe.Quote(source));
            ep.Append(" -filter_complex \"").Append(graph).Append("\" -map \"[v]\" -map \"[a]\" -vsync 0");
            ep.Append(" -c:v ").Append(videoCodec);
            if (videoCodec == TVideoCodec.mpeg2video) ep.Append(" -b:v 50M -minrate 50M -maxrate 50M -bufsize 17.8M -g 12 -bf 2 -dc 10");
            else ep.Append(" -b:v 25M -g 25 -bf 2");
            ep.Append(AdditionalParameters(additional));
            ep.Append(" -pix_fmt yuv422p -flags +ildct+ilme -top 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv -threads 2");
            ep.Append(" -c:a ").Append(audioCodec);
            if (audioCodec != TAudioCodec.pcm_s16le && audioCodec != TAudioCodec.pcm_s24le) ep.Append(" -b:a 256k");
            ep.Append(" -ar 48000 -ac 2");
            if (duration.HasValue) ep.Append(" -t ").Append(N(duration.Value));
            if (audio == null) ep.Append(" -shortest");
            ep.Append(" -timecode ").Append(timecode).Append(" -y ").Append(BroadcastProbe.Quote(destination));
            return ep.ToString();
        }
    }
}
