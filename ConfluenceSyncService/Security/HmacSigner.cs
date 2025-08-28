using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ConfluenceSyncService.Security
{
    public interface IHmacSigner
    {
        string Sign(string data);
        bool Verify(string data, string signature);
    }

    public sealed class AckLinkOptions
    {
        public string SigningKey { get; set; } = ""; // base64 or raw
        public int GraceDays { get; set; } = 1;
        public string BaseUrl { get; set; } = "https://localhost"; // host of this app for links. Use h ttps://localhost for dev
    }

    public sealed class HmacSigner(IOptions<AckLinkOptions> opts) : IHmacSigner
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
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
        }


        private static byte[] NormalizeKey(string k)
        {
            if (string.IsNullOrWhiteSpace(k)) return RandomNumberGenerator.GetBytes(32);
            try { return Convert.FromBase64String(k); } catch { return Encoding.UTF8.GetBytes(k); }
        }

        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

}
