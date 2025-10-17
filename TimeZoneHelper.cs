using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace RunCommandsService
{
    public static class TimeZoneHelper
    {
        // Comprehensive IANA -> Windows timezone mapping
        private static readonly Dictionary<string, string> IanaToWindows = new(StringComparer.OrdinalIgnoreCase)
        {
            // UTC variants
            { "UTC", "UTC" },
            { "Etc/UTC", "UTC" },
            { "Etc/GMT", "UTC" },
            { "GMT", "GMT Standard Time" },

            // Americas - North America
            { "America/New_York", "Eastern Standard Time" },
            { "America/Chicago", "Central Standard Time" },
            { "America/Denver", "Mountain Standard Time" },
            { "America/Phoenix", "US Mountain Standard Time" },
            { "America/Los_Angeles", "Pacific Standard Time" },
            { "America/Anchorage", "Alaskan Standard Time" },
            { "America/Adak", "Aleutian Standard Time" },
            { "Pacific/Honolulu", "Hawaiian Standard Time" },
            { "America/Halifax", "Atlantic Standard Time" },
            { "America/St_Johns", "Newfoundland Standard Time" },

            // Americas - Central America & Caribbean
            { "America/Mexico_City", "Central Standard Time (Mexico)" },
            { "America/Cancun", "Eastern Standard Time (Mexico)" },
            { "America/Santo_Domingo", "SA Western Standard Time" },
            { "America/Havana", "Cuba Standard Time" },
            { "America/Jamaica", "SA Pacific Standard Time" },

            // Americas - South America
            { "America/Sao_Paulo", "E. South America Standard Time" },
            { "America/Argentina/Buenos_Aires", "Argentina Standard Time" },
            { "America/Bogota", "SA Pacific Standard Time" },
            { "America/Lima", "SA Pacific Standard Time" },
            { "America/Caracas", "Venezuela Standard Time" },
            { "America/Santiago", "Pacific SA Standard Time" },
            { "America/La_Paz", "SA Western Standard Time" },

            // Europe - Western
            { "Europe/London", "GMT Standard Time" },
            { "Europe/Dublin", "GMT Standard Time" },
            { "Europe/Lisbon", "GMT Standard Time" },
            { "Atlantic/Reykjavik", "Greenwich Standard Time" },

            // Europe - Central
            { "Europe/Paris", "Romance Standard Time" },
            { "Europe/Berlin", "W. Europe Standard Time" },
            { "Europe/Amsterdam", "W. Europe Standard Time" },
            { "Europe/Brussels", "Romance Standard Time" },
            { "Europe/Madrid", "Romance Standard Time" },
            { "Europe/Rome", "W. Europe Standard Time" },
            { "Europe/Vienna", "W. Europe Standard Time" },
            { "Europe/Zurich", "W. Europe Standard Time" },
            { "Europe/Prague", "Central Europe Standard Time" },
            { "Europe/Warsaw", "Central European Standard Time" },
            { "Europe/Budapest", "Central Europe Standard Time" },
            { "Europe/Stockholm", "W. Europe Standard Time" },
            { "Europe/Copenhagen", "Romance Standard Time" },
            { "Europe/Oslo", "W. Europe Standard Time" },

            // Europe - Eastern
            { "Europe/Athens", "GTB Standard Time" },
            { "Europe/Bucharest", "GTB Standard Time" },
            { "Europe/Helsinki", "FLE Standard Time" },
            { "Europe/Kiev", "FLE Standard Time" },
            { "Europe/Moscow", "Russian Standard Time" },
            { "Europe/Istanbul", "Turkey Standard Time" },
            { "Europe/Minsk", "Belarus Standard Time" },

            // Asia - Middle East
            { "Asia/Dubai", "Arabian Standard Time" },
            { "Asia/Jerusalem", "Israel Standard Time" },
            { "Asia/Riyadh", "Arab Standard Time" },
            { "Asia/Baghdad", "Arabic Standard Time" },
            { "Asia/Tehran", "Iran Standard Time" },

            // Asia - South Asia
            { "Asia/Karachi", "Pakistan Standard Time" },
            { "Asia/Kolkata", "India Standard Time" },
            { "Asia/Kathmandu", "Nepal Standard Time" },
            { "Asia/Dhaka", "Bangladesh Standard Time" },
            { "Asia/Colombo", "Sri Lanka Standard Time" },

            // Asia - Southeast Asia
            { "Asia/Bangkok", "SE Asia Standard Time" },
            { "Asia/Singapore", "Singapore Standard Time" },
            { "Asia/Jakarta", "SE Asia Standard Time" },
            { "Asia/Manila", "Singapore Standard Time" },
            { "Asia/Kuala_Lumpur", "Singapore Standard Time" },
            { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" },

            // Asia - East Asia
            { "Asia/Tokyo", "Tokyo Standard Time" },
            { "Asia/Seoul", "Korea Standard Time" },
            { "Asia/Shanghai", "China Standard Time" },
            { "Asia/Hong_Kong", "China Standard Time" },
            { "Asia/Taipei", "Taipei Standard Time" },
            { "Asia/Ulaanbaatar", "Ulaanbaatar Standard Time" },

            // Pacific - Australia & New Zealand
            { "Australia/Sydney", "AUS Eastern Standard Time" },
            { "Australia/Melbourne", "AUS Eastern Standard Time" },
            { "Australia/Brisbane", "E. Australia Standard Time" },
            { "Australia/Adelaide", "Cen. Australia Standard Time" },
            { "Australia/Perth", "W. Australia Standard Time" },
            { "Australia/Darwin", "AUS Central Standard Time" },
            { "Pacific/Auckland", "New Zealand Standard Time" },
            { "Pacific/Fiji", "Fiji Standard Time" },

            // Pacific - Other
            { "Pacific/Guam", "West Pacific Standard Time" },
            { "Pacific/Samoa", "Samoa Standard Time" },
            { "Pacific/Tongatapu", "Tonga Standard Time" },

            // Africa
            { "Africa/Cairo", "Egypt Standard Time" },
            { "Africa/Johannesburg", "South Africa Standard Time" },
            { "Africa/Nairobi", "E. Africa Standard Time" },
            { "Africa/Lagos", "W. Central Africa Standard Time" },
            { "Africa/Casablanca", "Morocco Standard Time" },
        };

        private static ILogger _logger;

        /// <summary>
        /// Initialize the helper with a logger for diagnostics
        /// </summary>
        public static void Initialize(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Result of timezone resolution including warnings
        /// </summary>
        public class TimeZoneResult
        {
            public TimeZoneInfo TimeZone { get; set; }
            public string OriginalId { get; set; }
            public bool FellBackToUtc { get; set; }
            public bool WasIanaMapped { get; set; }
            public string ResolvedId { get; set; }
        }

        /// <summary>
        /// Find timezone with detailed result information
        /// </summary>
        public static TimeZoneResult FindTimeZoneWithResult(string id)
        {
            var result = new TimeZoneResult
            {
                OriginalId = id,
                FellBackToUtc = false,
                WasIanaMapped = false
            };

            if (string.IsNullOrWhiteSpace(id))
            {
                result.TimeZone = TimeZoneInfo.Utc;
                result.ResolvedId = "UTC";
                result.FellBackToUtc = true;
                return result;
            }

            // Try direct Windows timezone ID first
            try
            {
                result.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
                result.ResolvedId = id;
                return result;
            }
            catch
            {
                // Continue to IANA mapping
            }

            // Try IANA to Windows mapping
            if (IanaToWindows.TryGetValue(id, out var windowsId))
            {
                try
                {
                    result.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    result.ResolvedId = windowsId;
                    result.WasIanaMapped = true;
                    return result;
                }
                catch
                {
                    // Windows ID in map but not found on system - fall back to UTC
                }
            }

            // Fallback to UTC and log warning
            result.TimeZone = TimeZoneInfo.Utc;
            result.ResolvedId = "UTC";
            result.FellBackToUtc = true;

            _logger?.LogWarning(
                "TimeZone '{TimeZoneId}' not recognized. Falling back to UTC. " +
                "Please use Windows timezone IDs (e.g., 'Eastern Standard Time') or supported IANA IDs. " +
                "See https://docs.microsoft.com/en-us/windows-hardware/manufacture/desktop/default-time-zones",
                id);

            return result;
        }

        /// <summary>
        /// Find timezone (backward compatible method)
        /// </summary>
        public static TimeZoneInfo FindTimeZone(string id)
        {
            return FindTimeZoneWithResult(id).TimeZone;
        }

        /// <summary>
        /// Validate if a timezone ID is valid
        /// </summary>
        public static bool IsValidTimeZone(string id, out string error)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "TimeZone ID cannot be empty";
                return false;
            }

            // Try direct Windows lookup
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(id);
                error = null;
                return true;
            }
            catch
            {
                // Continue to IANA check
            }

            // Check IANA mapping
            if (IanaToWindows.TryGetValue(id, out var windowsId))
            {
                try
                {
                    TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    error = null;
                    return true;
                }
                catch
                {
                    error = $"IANA timezone '{id}' maps to Windows ID '{windowsId}' but that timezone is not available on this system";
                    return false;
                }
            }

            error = $"TimeZone '{id}' is not recognized. Use Windows timezone IDs or supported IANA IDs.";
            return false;
        }

        /// <summary>
        /// Get all supported IANA timezone IDs
        /// </summary>
        public static IReadOnlyCollection<string> GetSupportedIanaIds()
        {
            return IanaToWindows.Keys;
        }
    }
}

