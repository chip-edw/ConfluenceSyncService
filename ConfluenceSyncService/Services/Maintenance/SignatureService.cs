using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Text;

namespace ConfluenceSyncService.Services.Maintenance;

public sealed class SignatureService
{
    private readonly byte[] _key;

    public SignatureService(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new ArgumentException("LinkSigningKey is missing.", nameof(base64Key));
        _key = Convert.FromBase64String(base64Key);
    }

    public string Sign(string action, string resourceId, long expUnix)
    {
        var payload = $"{action}|{resourceId}|{expUnix}";
        using var h = new HMACSHA256(_key);
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return WebEncoders.Base64UrlEncode(hash);
    }

    public bool Validate(string action, string resourceId, long expUnix, string sig)
    {
        if (string.IsNullOrWhiteSpace(sig)) return false;

        // Expiry check first (cheap reject)
        if (DateTimeOffset.FromUnixTimeSeconds(expUnix) <= DateTimeOffset.UtcNow) return false;

        var expected = Sign(action, resourceId, expUnix);

        try
        {
            var a = WebEncoders.Base64UrlDecode(expected);
            var b = WebEncoders.Base64UrlDecode(sig);
            if (a is null || b is null || a.Length != b.Length) return false;

            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException)
        {
            // Malformed base64url in sig
            return false;
        }
    }

}

