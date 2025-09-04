using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ConfluenceSyncService.Security
{
    public interface IHmacSigner
    {
        string Sign(string data);
        bool Verify(string data, string signature);
    }

    /// <summary>
    /// Signer configuration: only what the signer needs.
    /// Keep policy knobs (RequireLatestLink, TTLs, etc.) in ConfluenceSyncService.Options.AckLinkOptions.
    /// </summary>
    public sealed class AckSignerOptions
    {
        /// <summary>Base64 or raw secret. If blank, a random key will be used (not recommended for production).</summary>
        public string SigningKey { get; set; } = "";

        /// <summary>Optional base URL used by the link generator (may be overridden elsewhere).</summary>
        public string BaseUrl { get; set; } = "https://localhost";

        /// <summary>Legacy carry-over (if you used it in link gen). Safe to leave.</summary>
        public int GraceDays { get; set; } = 1;
    }

    /// <summary>
    /// HMAC-SHA256 signer used for ACK links.
    /// </summary>
    public sealed class HmacSigner(IOptions<AckSignerOptions> opts) : IHmacSigner
    {
        private readonly byte[] _key = NormalizeKey(opts.Value.SigningKey);

        public string Sign(string data)
        {
            using var h = new HMACSHA256(_key);
            var sig = h.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Base64UrlEncode(sig);
        }

        public bool Verify(string data, string signature)
        {
            var expected = Sign(data);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(signature));
        }

        private static byte[] NormalizeKey(string k)
        {
            if (string.IsNullOrWhiteSpace(k)) return RandomNumberGenerator.GetBytes(32);
            try { return Convert.FromBase64String(k); }
            catch { return Encoding.UTF8.GetBytes(k); }
        }

        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
