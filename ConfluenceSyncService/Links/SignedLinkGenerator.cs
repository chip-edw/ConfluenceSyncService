using System.Web;
using ConfluenceSyncService.Security;
using ConfluenceSyncService.Time;
using Microsoft.Extensions.Options;


namespace ConfluenceSyncService.Links
{

    public interface ISignedLinkGenerator
    {
        string GenerateMarkCompleteLink(string id, string region, int durationBusinessDays, int graceDays, DateTime anchorUtc, int startOffsetBusinessDays, TimeOnly regionCutoffLocal, string? correlationId = null);
    }


    public sealed class SignedLinkGenerator(IHmacSigner signer, IOptions<AckLinkOptions> ackOpts, IRegionDueCalculator dueCalc) : ISignedLinkGenerator
    {
        private readonly AckLinkOptions _opts = ackOpts.Value;


        public string GenerateMarkCompleteLink(string id, string region, int durationBusinessDays, int graceDays, DateTime anchorUtc, int startOffsetBusinessDays, TimeOnly regionCutoffLocal, string? correlationId = null)
        {
            var dueUtc = dueCalc.ComputeDueUtc(anchorUtc, startOffsetBusinessDays, durationBusinessDays, region, regionCutoffLocal);
            var expUtc = BusinessDayHelper.AddBusinessDaysUtc(dueUtc, graceDays);
            var exp = new DateTimeOffset(expUtc).ToUnixTimeSeconds();
            var data = $"id={id}&exp={exp}";

            if (!string.IsNullOrEmpty(correlationId)) data += $"&c={HttpUtility.UrlEncode(correlationId)}";

            var sig = signer.Sign(data);
            var qs = $"{data}&sig={sig}";

            return $"{_opts.BaseUrl.TrimEnd('/')}/maintenance/actions/mark-complete?{qs}";


        }
    }

}
