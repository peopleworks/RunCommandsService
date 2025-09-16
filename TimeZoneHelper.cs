using System;
using System.Collections.Generic;

namespace RunCommandsService
{
    public static class TimeZoneHelper
    {
        // Minimal IANA -> Windows map for common zones used here; extend as needed.
        private static readonly Dictionary<string, string> IanaToWindows = new(StringComparer.OrdinalIgnoreCase)
        {
            { "UTC", "UTC" },
            { "Etc/UTC", "UTC" },
            { "Etc/GMT", "UTC" },
            { "America/New_York", "Eastern Standard Time" },
            { "America/Chicago", "Central Standard Time" },
            { "America/Denver", "Mountain Standard Time" },
            { "America/Los_Angeles", "Pacific Standard Time" },
            { "Europe/London", "GMT Standard Time" },
            { "Europe/Paris", "Romance Standard Time" },
            { "Europe/Berlin", "W. Europe Standard Time" },
            { "Europe/Zurich", "W. Europe Standard Time" },
            { "America/Santo_Domingo", "SA Western Standard Time" },
        };

        public static TimeZoneInfo FindTimeZone(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                if (IanaToWindows.TryGetValue(id, out var win))
                {
                    try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
                }
                return TimeZoneInfo.Utc;
            }
        }
    }
}

