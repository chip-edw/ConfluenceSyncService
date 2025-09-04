namespace ConfluenceSyncService.Time
{
    public static class BusinessDayHelper
    {
        public static DateTime AddBusinessDaysUtc(DateTime utcStart, int businessDays)
        {
            var dt = utcStart;
            var step = Math.Sign(businessDays);
            var remaining = Math.Abs(businessDays);

            while (remaining > 0)
            {
                dt = dt.AddDays(step);
                if (IsBusinessDay(dt)) remaining--;
            }
            return dt;
        }

        public static bool IsBusinessDay(DateTime dtUtc)
        {
            var day = dtUtc.DayOfWeek;
            return day != DayOfWeek.Saturday && day != DayOfWeek.Sunday;
        }

        // Add inside your existing BusinessDayHelper class
        public static DateTimeOffset NextBusinessDayAtHourUtc(string? regionOrTz, int sendHourLocal, DateTimeOffset fromUtc)
        {
            if (sendHourLocal < 0) sendHourLocal = 0;
            if (sendHourLocal > 23) sendHourLocal = 23;

            static TimeZoneInfo ResolveTz(string? id)
            {
                // Region shortcuts → IANA TZ
                var regionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AMER"] = "America/Chicago",
                    ["AMERICAS"] = "America/Chicago",
                    ["NA"] = "America/Chicago",
                    ["EMEA"] = "Europe/London",
                    ["EU"] = "Europe/London",
                    ["APAC"] = "Asia/Singapore",
                    ["APJ"] = "Asia/Singapore",
                    ["AUS"] = "Australia/Sydney",
                    // ✅ New Zealand shortcuts
                    ["NZ"] = "Pacific/Auckland",
                    ["NZL"] = "Pacific/Auckland",
                    ["NEW ZEALAND"] = "Pacific/Auckland",
                    ["AUCKLAND"] = "Pacific/Auckland",
                    ["WELLINGTON"] = "Pacific/Auckland"
                };

                string? candidate = id;
                if (!string.IsNullOrWhiteSpace(id) && regionMap.TryGetValue(id.Trim(), out var mapped))
                    candidate = mapped;

                // Try candidate directly
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
                    catch { /* continue */ }
                }

                // IANA → Windows IDs (for Windows hosts)
                var ianaToWindows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["America/Chicago"] = "Central Standard Time",
                    ["America/New_York"] = "Eastern Standard Time",
                    ["America/Denver"] = "Mountain Standard Time",
                    ["America/Los_Angeles"] = "Pacific Standard Time",
                    ["America/Phoenix"] = "US Mountain Standard Time",
                    ["Europe/London"] = "GMT Standard Time",
                    ["Europe/Paris"] = "Romance Standard Time",
                    ["Asia/Singapore"] = "Singapore Standard Time",
                    ["Asia/Kolkata"] = "India Standard Time",
                    ["Australia/Sydney"] = "AUS Eastern Standard Time",
                    // ✅ New Zealand IANA → Windows
                    ["Pacific/Auckland"] = "New Zealand Standard Time"
                };

                if (!string.IsNullOrWhiteSpace(candidate) && ianaToWindows.TryGetValue(candidate, out var winId))
                {
                    try { return TimeZoneInfo.FindSystemTimeZoneById(winId); }
                    catch { /* continue */ }
                }

                return TimeZoneInfo.Utc;
            }

            static bool IsBusinessDay(DayOfWeek d) => d != DayOfWeek.Saturday && d != DayOfWeek.Sunday;

            var tz = ResolveTz(regionOrTz);
            var localNow = TimeZoneInfo.ConvertTime(fromUtc, tz);

            var localDate = localNow.Date.AddDays(1);
            while (!IsBusinessDay(localDate.DayOfWeek))
                localDate = localDate.AddDays(1);

            var localTarget = new DateTime(localDate.Year, localDate.Month, localDate.Day, sendHourLocal, 0, 0, DateTimeKind.Unspecified);
            var localOffset = tz.GetUtcOffset(localTarget);
            var localDto = new DateTimeOffset(localTarget, localOffset);
            return localDto.ToUniversalTime();
        }


        public static bool IsWithinWindow(string? regionOrTz, int startHourLocal, int endHourLocal, int cushionHours, DateTimeOffset nowUtc)
        {
            TimeZoneInfo ResolveTz(string? id)
            {
                var regionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AMER"] = "America/Chicago",
                    ["AMERICAS"] = "America/Chicago",
                    ["NA"] = "America/Chicago",
                    ["EMEA"] = "Europe/London",
                    ["EU"] = "Europe/London",
                    ["APAC"] = "Asia/Singapore",
                    ["APJ"] = "Asia/Singapore",
                    ["AUS"] = "Australia/Sydney",
                    // ✅ New Zealand shortcuts
                    ["NZ"] = "Pacific/Auckland",
                    ["NZL"] = "Pacific/Auckland",
                    ["NEW ZEALAND"] = "Pacific/Auckland",
                    ["AUCKLAND"] = "Pacific/Auckland",
                    ["WELLINGTON"] = "Pacific/Auckland"
                };

                string? cand = id;
                if (!string.IsNullOrWhiteSpace(id) && regionMap.TryGetValue(id.Trim(), out var mapped))
                    cand = mapped;

                try { return TimeZoneInfo.FindSystemTimeZoneById(cand ?? "UTC"); } catch { }

                var i2w = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["America/Chicago"] = "Central Standard Time",
                    ["America/New_York"] = "Eastern Standard Time",
                    ["America/Denver"] = "Mountain Standard Time",
                    ["America/Los_Angeles"] = "Pacific Standard Time",
                    ["America/Phoenix"] = "US Mountain Standard Time",
                    ["Europe/London"] = "GMT Standard Time",
                    ["Europe/Paris"] = "Romance Standard Time",
                    ["Asia/Singapore"] = "Singapore Standard Time",
                    ["Asia/Kolkata"] = "India Standard Time",
                    ["Australia/Sydney"] = "AUS Eastern Standard Time",
                    // ✅ New Zealand
                    ["Pacific/Auckland"] = "New Zealand Standard Time"
                };
                if (!string.IsNullOrWhiteSpace(cand) && i2w.TryGetValue(cand, out var winId))
                {
                    try { return TimeZoneInfo.FindSystemTimeZoneById(winId); } catch { }
                }

                return TimeZoneInfo.Utc;
            }

            static bool IsBusinessDay(DayOfWeek d) => d != DayOfWeek.Saturday && d != DayOfWeek.Sunday;

            var tz = ResolveTz(regionOrTz);
            var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);

            if (!IsBusinessDay(localNow.DayOfWeek)) return false;

            startHourLocal = Math.Clamp(startHourLocal, 0, 23);
            endHourLocal = Math.Clamp(endHourLocal, 0, 23);

            var start = localNow.Date.AddHours(startHourLocal);
            var end = localNow.Date.AddHours(endHourLocal);

            // (Optional) apply cushion logic here if you later decide to narrow the window using cushionHours
            return localNow >= start && localNow < end;
        }



    }

}
