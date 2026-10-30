// EWTN native archive preparation policy. GPL-2.0.
using System;
using System.Collections.Generic;

namespace TAS.Server
{
    internal static class ArchivePreparationRules
    {
        internal static bool InWindow(DateTime now, DateTime due, int hours, bool entire)
        {
            if (hours <= 0) throw new ArgumentOutOfRangeException(nameof(hours));
            return entire || ((due - now).TotalMinutes >= -10 && (due - now).TotalHours <= hours);
        }
        internal static bool HasSpace(ulong bytes, long free, IEnumerable<ulong> active)
        {
            if (free <= 0) return false;
            try
            {
                checked
                {
                    var required = bytes + 64UL * 1024 * 1024;
                    foreach (var reserved in active) required += reserved;
                    return required <= (ulong)free;
                }
            }
            catch (OverflowException) { return false; }
        }
        internal static DateTime NextRetry(DateTime now, int attempts)
            => now.AddSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(5, Math.Max(0, attempts - 1)))));
    }
}
