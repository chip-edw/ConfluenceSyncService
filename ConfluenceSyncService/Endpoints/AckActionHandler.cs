using ConfluenceSyncService.Identity;
using ConfluenceSyncService.Options;
using ConfluenceSyncService.Security;
using ConfluenceSyncService.SharePoint;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;


namespace ConfluenceSyncService.Endpoints
{
    public sealed class AckActionHandler(
        IHmacSigner signer,
        IClickerIdentityProvider identityProvider,
        ISharePointTaskUpdater sp,
        IOptions<AckLinkOptions> ackOpts,
        ILogger<AckActionHandler> log)
    {
        public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
        {
            var q = ctx.Request.Query;
            var id = q["id"].ToString();
            var exp = long.TryParse(q["exp"], out var e) ? e : 0;
            var sig = q["sig"].ToString();
            var corr = q["c"].ToString();
            var listId = q["list"].ToString(); // optional, fallback to config or default list
            if (string.IsNullOrWhiteSpace(id) || exp == 0 || string.IsNullOrWhiteSpace(sig))
                return Results.BadRequest("Missing required parameters.");

            var data = $"id={id}&exp={exp}" + (string.IsNullOrEmpty(corr) ? "" : $"&c={corr}");
            if (!signer.Verify(data, sig)) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > exp) return Results.StatusCode(StatusCodes.Status410Gone);

            var who = await identityProvider.GetIdentityAsync(ctx, ct);
            var ackBy = who?.DisplayName ?? "unknown";
            var ackActual = who?.Email ?? who?.Upn;

            try
            {
                // Idempotent: attempt to mark complete, tolerate already completed
                var ok = await sp.MarkCompletedAsync(listId, id, ackBy, ackActual, ct);
                if (!ok) log.LogWarning("MarkCompleted returned false for item {ItemId}", id);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to mark complete for item {ItemId}", id);
                // Still return 200 to keep clicker UX resilient
            }
            return Results.Text("Acknowledged. You can close this window.");
        }
    }
}
