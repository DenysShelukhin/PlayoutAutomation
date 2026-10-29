// EWTN modifications, 2026-10-29. GPL-2.0; see LICENSE.
using TAS.Common;

namespace TAS.Server.MediaOperation
{
    internal static class BroadcastPlayout
    {
        // The existing connector emits Clipname as an AMCP producer specification.
        // Expand chroma within each field before the TVP GPU mixer samples it.
        // This is a fixed trusted argument, never user-supplied filter text.
        internal static string Producer(string quotedName, bool tvp, bool movie,
            TVideoFormat source, TVideoFormat target)
        {
            return tvp && movie && source == TVideoFormat.HD1080i5000 && target == TVideoFormat.HD1080i5000
                ? quotedName + " FILTER \"scale=iw:ih:interl=1,format=yuv422p\""
                : quotedName;
        }
    }
}
