using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ConfluenceSyncService.Identity
{
    public sealed class ClickerIdentityOptions
    {
        public IdentityMode Mode { get; set; } = IdentityMode.JwtBearer;
        public HeaderSsoOptions HeaderSso { get; set; } = new();
    }

    public sealed class HeaderSsoOptions
    {
        public string EmailHeader { get; set; } = "X-User-Email";
        public string NameHeader { get; set; } = "X-User-Name";
        public string UpnHeader { get; set; } = "X-User-UPN";
    }

    public sealed class ClickerIdentityProvider(IOptions<ClickerIdentityOptions> opts) : IClickerIdentityProvider
    {
        private readonly ClickerIdentityOptions _opts = opts.Value;
        public IdentityMode Mode => _opts.Mode;

        public ValueTask<ClickerIdentity?> GetIdentityAsync(HttpContext httpContext, CancellationToken ct = default)
            => Mode switch
            {
                IdentityMode.JwtBearer => ValueTask.FromResult(FromClaims(httpContext.User)),
                IdentityMode.HeaderSso => ValueTask.FromResult(FromHeaders(httpContext)),
                IdentityMode.EasyAuth => ValueTask.FromResult(FromEasyAuth(httpContext)),
                _ => ValueTask.FromResult<ClickerIdentity?>(null)
            };
        private ClickerIdentity? FromClaims(ClaimsPrincipal user)
        {
            if (user?.Identity is not { IsAuthenticated: true }) return null;
            var name = user.FindFirst("name")?.Value ?? user.Identity?.Name ?? user.FindFirst(ClaimTypes.Name)?.Value;
            var upn = user.FindFirst("upn")?.Value ?? user.FindFirst(ClaimTypes.Upn)?.Value;
            var email = user.FindFirst("preferred_username")?.Value
                ?? user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.FindFirst("emails")?.Value;
            return new ClickerIdentity(name ?? email ?? upn ?? "unknown", upn, email);
        }
        private ClickerIdentity? FromHeaders(HttpContext ctx)
        {
            var h = _opts.HeaderSso;
            ctx.Request.Headers.TryGetValue(h.EmailHeader, out var email);
            ctx.Request.Headers.TryGetValue(h.NameHeader, out var name);
            ctx.Request.Headers.TryGetValue(h.UpnHeader, out var upn);
            var display = string.IsNullOrWhiteSpace(name) ? (string?)email ?? upn.ToString() : name.ToString();
            return string.IsNullOrWhiteSpace(display) ? null : new ClickerIdentity(display!, upn.ToString(), email.ToString());
        }

        // Azure App Service EasyAuth: X-MS-CLIENT-PRINCIPAL header (base64 JSON)
        private ClickerIdentity? FromEasyAuth(HttpContext ctx)
        {
            if (!ctx.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var raw)) return null;
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw!));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? claim(string type)
                => root.GetProperty("claims").EnumerateArray()
                .FirstOrDefault(c => c.GetProperty("typ").GetString()?.EndsWith(type, StringComparison.OrdinalIgnoreCase) == true)
                .TryGetProperty("val", out var v) ? v.GetString() : null;
            var name = claim("name") ?? claim(ClaimTypes.Name);
            var upn = claim("upn") ?? claim(ClaimTypes.Upn);
            var email = claim("email") ?? claim(ClaimTypes.Email);
            return new ClickerIdentity(name ?? email ?? upn ?? "unknown", upn, email);
        }
    }
}
