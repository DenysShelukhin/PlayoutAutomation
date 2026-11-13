using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace TAS.Server
{
    // AMCP 2.0/TVP and official 2.2+ expose different physical state trees.
    public static class CasparPhysicalInfo
    {
        public static long CommandPosition(long frames, bool modern, bool interlaced)
            => checked(frames * (modern && interlaced ? 2L : 1L));

        public static bool TryForeground(XmlDocument xml, int layer, long frameTicks, out string path, out long frame)
        {
            path = null; frame = -1;
            var producer = xml.SelectSingleNode("/channel/stage/layers/layer[index=" + layer + "]/foreground/producer");
            if (producer?.SelectSingleNode("type")?.InnerText == "transition-producer")
                producer = producer.SelectSingleNode("destination/producer");
            if (producer != null) {
                path = producer.SelectSingleNode("filename")?.InnerText;
                return !string.IsNullOrEmpty(path) && long.TryParse(producer.SelectSingleNode("file-frame-number")?.InnerText,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out frame) && frame >= 0;
            }
            var file = xml.SelectSingleNode("/channel/stage/layer/layer_" + layer + "/foreground/file");
            path = file?.SelectSingleNode("path")?.InnerText;
            decimal seconds;
            if (string.IsNullOrEmpty(path) || frameTicks <= 0 || !decimal.TryParse(file.SelectSingleNode("time[1]")?.InnerText,
                NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds < 0 || seconds > long.MaxValue / (decimal)TimeSpan.TicksPerSecond) return false;
            frame = decimal.ToInt64(decimal.Floor(seconds * TimeSpan.TicksPerSecond / frameTicks));
            return true;
        }

        public static IEnumerable<string> ProducerPaths(XmlDocument xml)
        {
            foreach (XmlNode node in xml.SelectNodes("//filename | /channel/stage/layer/*/foreground/file/path | /channel/stage/layer/*/background/file/path"))
                if (!string.IsNullOrWhiteSpace(node.InnerText)) yield return node.InnerText;
        }
    }
}
