using ConfluenceSyncService.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace ConfluenceSyncService.Common.Secrets
{
    public class SqliteSecretsProvider : ISecretsProvider, IInitializableSecretsProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SqliteSecretsProvider> _logger;
        private readonly ConcurrentDictionary<string, string> _secretCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _isInitialized = false;
        private readonly object _initLock = new object();

        public SqliteSecretsProvider(IServiceProvider serviceProvider, ILogger<SqliteSecretsProvider> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            lock (_initLock)
            {
                if (_isInitialized) return;

                try
                {
                    // Create a scope to get the DbContext
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Load secrets synchronously within the lock
                    var secrets = dbContext.ConfigStore.ToList();
                    foreach (var entry in secrets)
                    {
                        _secretCache[entry.ValueName] = entry.Value;
                    }

                    _isInitialized = true;
                    _logger.LogInformation("SqliteSecretsProvider initialized. {Count} secrets cached.", _secretCache.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to preload secrets from ConfigStore.");
                    throw;
                }
            }
        }

        public Task<string?> GetApiKeyAsync(string keyName)
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("SqliteSecretsProvider must be initialized before use. Call InitializeAsync() first.");
            }

            if (_secretCache.TryGetValue(keyName, out var value))
            {
                _logger.LogDebug("Retrieved API key from memory cache: {KeyName}", keyName);
                return Task.FromResult<string?>(value);
            }

            _logger.LogWarning("Secret '{KeyName}' not found in memory cache.", keyName);
            return Task.FromResult<string?>(null);
        }

        public Task<Dictionary<string, string>> GetAllApiKeysAsync()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("SqliteSecretsProvider must be initialized before use. Call InitializeAsync() first.");
            }

            var copy = new Dictionary<string, string>(_secretCache, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(copy);
        }

        public async Task SaveRefreshTokenAsync(string keyName, string newRefreshToken)
        {
            // Create a scope for database operations
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var entry = await dbContext.ConfigStore.FirstOrDefaultAsync(c => c.ValueName == keyName);
            if (entry != null)
            {
                if (entry.Value == newRefreshToken)
                {
                    _logger.LogWarning("Refresh token for '{KeyName}' is unchanged. Skipping update.", keyName);
                    return;
                }
                _logger.LogInformation("Updating refresh token for key '{KeyName}'.", keyName);
                entry.Value = newRefreshToken;
            }
            else
            {
                _logger.LogInformation("Creating new refresh token entry for key '{KeyName}'.", keyName);
                entry = new ConfigStore
                {
                    ValueName = keyName,
                    Value = newRefreshToken,
                    ValueType = "RefreshToken",
                    Description = $"Refresh token for {keyName}"
                };
                dbContext.ConfigStore.Add(entry);
            }

            await dbContext.SaveChangesAsync();

            // Update cache
            _secretCache[keyName] = newRefreshToken;
            _logger.LogInformation("Refresh token saved and memory cache updated for key '{KeyName}'.", keyName);
        }
    }
}