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
    }

}
