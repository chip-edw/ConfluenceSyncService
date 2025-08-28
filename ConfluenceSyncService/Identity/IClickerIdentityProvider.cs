using Microsoft.AspNetCore.Http;

namespace ConfluenceSyncService.Identity
{
    public enum IdentityMode { EasyAuth, JwtBearer, HeaderSso }
    public sealed record ClickerIdentity(string DisplayName, string? Upn, string? Email);

    public interface IClickerIdentityProvider
    {
        IdentityMode Mode { get; }
        ValueTask<ClickerIdentity?> GetIdentityAsync(HttpContext httpContext, CancellationToken ct = default);
    }
}
