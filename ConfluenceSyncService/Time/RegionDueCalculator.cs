using Microsoft.Extensions.Options;


namespace ConfluenceSyncService.Time
{

    public sealed class RegionOffsetsOptions
    {
        // Example: { "NA": -5, "EMEA": 1, "APAC": 10 } hours from UTC
        public Dictionary<string, int> Offsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }


    public interface IRegionDueCalculator
    {
        DateTime ComputeDueUtc(DateTime anchorUtc, int startOffsetBusinessDays, int durationBusinessDays, string regionCode, TimeOnly regionCutoffLocal);
    }

    public sealed class RegionDueCalculator(IOptions<RegionOffsetsOptions> opts) : IRegionDueCalculator
    {
        private readonly RegionOffsetsOptions _opts = opts.Value;

        public DateTime ComputeDueUtc(DateTime anchorUtc, int startOffsetBusinessDays, int durationBusinessDays, string regionCode, TimeOnly regionCutoffLocal)
        {
            var startUtc = BusinessDayHelper.AddBusinessDaysUtc(anchorUtc, startOffsetBusinessDays);
            var durationEndUtc = BusinessDayHelper.AddBusinessDaysUtc(startUtc, durationBusinessDays);
            var offsetHours = _opts.Offsets.TryGetValue(regionCode, out var o) ? o : 0;
            var local = new DateTimeOffset(durationEndUtc).ToOffset(TimeSpan.FromHours(offsetHours));
            var localWithCutoff = new DateTimeOffset(local.Year, local.Month, local.Day, regionCutoffLocal.Hour, regionCutoffLocal.Minute, 0, local.Offset);
            return localWithCutoff.UtcDateTime;
        }

    }

}
