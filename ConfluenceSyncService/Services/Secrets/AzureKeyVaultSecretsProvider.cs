using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using ConfluenceSyncService.Common.Secrets;
using Serilog;

namespace ConfluenceSyncService.Services.Secrets
{
    public class AzureKeyVaultSecretsProvider : ISecretsProvider, IInitializableSecretsProvider
    {
        private readonly SecretClient _secretClient;
        private readonly Serilog.ILogger _logger;
        private readonly Dictionary<string, string> _secretCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _isInitialized = false;
        private readonly object _initLock = new object();

        public AzureKeyVaultSecretsProvider(IConfiguration configuration)
        {
            var keyVaultUri = configuration["SecretsProvider:AzureKeyVaultUri"];
            if (string.IsNullOrWhiteSpace(keyVaultUri))
                throw new ArgumentException("Missing AzureKeyVaultUri in configuration.");

            _secretClient = new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential());
            _logger = Log.ForContext<AzureKeyVaultSecretsProvider>();
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            lock (_initLock)
            {
                if (_isInitialized) return;
            }

            try
            {
                _logger.Information("Initializing Azure Key Vault secrets into memory...");

                await foreach (var secretProperties in _secretClient.GetPropertiesOfSecretsAsync())
                {
                    try
                    {
                        // Only process enabled secrets
                        if (secretProperties.Enabled != true)
                        {
                            _logger.Debug("Skipping disabled secret '{SecretName}'", secretProperties.Name);
                            continue;
                        }

                        var secret = await _secretClient.GetSecretAsync(secretProperties.Name);
                        if (secret.Value?.Value != null)
                        {
                            _secretCache[secretProperties.Name] = secret.Value.Value;
                            _logger.Debug("Cached secret '{SecretName}'", secretProperties.Name);
                        }
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                    {
                        _logger.Warning("Secret '{SecretName}' listed but not found.", secretProperties.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error retrieving secret '{SecretName}'", secretProperties.Name);
                    }
                }

                lock (_initLock)
                {
                    _isInitialized = true;
                }

                _logger.Information("AzureKeyVaultSecretsProvider initialized. {Count} secrets cached.", _secretCache.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to preload secrets from Azure Key Vault.");
                throw;
            }
        }

        public Task<string?> GetApiKeyAsync(string key)
        {
            if (!_isInitialized)
            {
                _logger.Warning("AzureKeyVaultSecretsProvider not initialized when accessing key '{Key}'", key);
            }

            if (_secretCache.TryGetValue(key, out var value))
            {
                _logger.Debug("Retrieved secret '{Key}' from memory cache", key);
                return Task.FromResult<string?>(value);
            }

            _logger.Warning("Secret '{Key}' not found in memory cache.", key);
            return Task.FromResult<string?>(null);
        }

        public Task<Dictionary<string, string>> GetAllApiKeysAsync()
        {
            if (!_isInitialized)
            {
                _logger.Warning("AzureKeyVaultSecretsProvider not initialized when getting all keys");
            }

            // Return a shallow copy of the in-memory cache
            return Task.FromResult(new Dictionary<string, string>(_secretCache, StringComparer.OrdinalIgnoreCase));
        }

        public async Task SaveRefreshTokenAsync(string key, string value)
        {
            try
            {
                await _secretClient.SetSecretAsync(key, value);
                _secretCache[key] = value;
                _logger.Information("Secret '{Key}' saved to Azure Key Vault and memory cache updated.", key);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save secret '{Key}' to Azure Key Vault.", key);
                throw;
            }
        }
    }
}