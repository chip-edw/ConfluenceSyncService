using System.Security.Cryptography;
using System.Text;
using ConfluenceSyncService.Common.Constants;
using ConfluenceSyncService.Common.Secrets;

namespace ConfluenceSyncService.Security
{
    /// <summary>
    /// IHmacSigner that loads HMAC key from the configured ISecretsProvider (AKV or SQLite),
    /// with periodic refresh so rotations don't require an app restart.
    /// </summary>
    public sealed class SecretsBackedHmacSigner : IHmacSigner
    {
        private readonly ISecretsProvider _secrets;
        private readonly ILogger<SecretsBackedHmacSigner> _log;

        // hot-swappable cache
        private volatile byte[]? _key;
        private long _nextReloadTicks = 0; // UTC ticks for next reload
        private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(60); // adjust if you like
        private readonly object _gate = new();

        public SecretsBackedHmacSigner(ISecretsProvider secrets, ILogger<SecretsBackedHmacSigner> log)
        {
            _secrets = secrets;
            _log = log;
        }

        private byte[] GetKey()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var key = _key;
            var nextTicks = Volatile.Read(ref _nextReloadTicks);
            if (key is not null && nowTicks < nextTicks) return key;

            lock (_gate)
            {
                nextTicks = Volatile.Read(ref _nextReloadTicks);
                if (_key is not null && nowTicks < nextTicks) return _key;

                var raw = _secrets.GetApiKeyAsync(SecretsKeys.LinkSigningKey).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(raw))
                    throw new InvalidOperationException($"Secret '{SecretsKeys.LinkSigningKey}' is missing.");

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(raw);
                }
                catch
                {
                    // allow UTF-8 in dev; prefer base64 in prod
                    bytes = Encoding.UTF8.GetBytes(raw);
                }

                var next = DateTime.UtcNow.Add(_refreshInterval).Ticks;
                Volatile.Write(ref _nextReloadTicks, next);
                _log.LogDebug("HMAC key loaded (len {Len}). Next reload at {NextReload:u}",
                bytes.Length, new DateTime(next, DateTimeKind.Utc));

                return bytes;
            }
        }

        public string Sign(string data)
        {
            using var h = new HMACSHA256(GetKey());
            var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_'); // base64url
        }

        public bool Verify(string data, string signature)
        {
            if (string.IsNullOrWhiteSpace(signature)) return false;
            var expected = Sign(data);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(signature));
        }
    }
}
