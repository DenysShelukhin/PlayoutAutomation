// EWTN 2026-11-09. Decimal GB, checked reservations, UTC schedule window.
using System;
namespace TAS.Server
{
    public static class PlayoutCacheRules
    {
        public static bool Request(TAS.Common.Interfaces.Media.IMedia media)
            => media is TAS.Server.Media.MediaBase local && PlayoutCache.CanServe(local,true);
        public static bool Ready(TAS.Common.Interfaces.Media.IMedia media)
            => !(media is TAS.Server.Media.MediaBase local) || PlayoutCache.CanServe(local,false);
        public const long HardMaximum = 200000000000;
        public static bool InWindow(DateTime now, DateTime due, int hours) => hours > 0 && hours <= 72 && due >= now && due <= now.AddHours(hours);
        public static bool CanDisplace(DateTime cachedUse, DateTime incomingUse, DateTime now)
            => cachedUse > incomingUse && cachedUse > now.AddMinutes(5);
        public static bool Fits(long used, long reserved, long incoming, long maximum) =>
            maximum > 0 && maximum <= HardMaximum && used >= 0 && reserved >= 0 && incoming >= 0 &&
            used <= maximum && reserved <= maximum - used && incoming <= maximum - used - reserved;
    }
}
