using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using ConfluenceSyncService.Common.Secrets;
using Serilog;

namespace ConfluenceSyncService.Services.Secrets
{
    public class AzureKeyVaultSecretsProvider : ISecretsProvider
    {
        private readonly SecretClient _secretClient;
        private readonly Serilog.ILogger _logger;

        public AzureKeyVaultSecretsProvider(IConfiguration configuration)
        {
            var keyVaultUri = configuration["SecretsProvider:AzureKeyVaultUri"];

            if (string.IsNullOrWhiteSpace(keyVaultUri))
                throw new ArgumentException("Missing AzureKeyVaultUri in configuration.");

            _secretClient = new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential());
            _logger = Log.ForContext<AzureKeyVaultSecretsProvider>();
        }

        public async Task<string?> GetApiKeyAsync(string key)
        {
            try
            {
                var response = await _secretClient.GetSecretAsync(key);
                return response.Value?.Value;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.Warning("Secret '{Key}' not found in Azure Key Vault.", key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to retrieve secret '{Key}' from Azure Key Vault.", key);
                throw;
            }
        }

        public async Task SaveRefreshTokenAsync(string key, string value)
        {
            try
            {
                await _secretClient.SetSecretAsync(key, value);
                _logger.Information("Secret '{Key}' saved to Azure Key Vault.", key);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save secret '{Key}' to Azure Key Vault.", key);
                throw;
            }
        }

        public async Task<Dictionary<string, string>> GetAllApiKeysAsync()
        {
            var result = new Dictionary<string, string>();

            try
            {
                await foreach (var secretProperties in _secretClient.GetPropertiesOfSecretsAsync())
                {
                    try
                    {
                        var secret = await _secretClient.GetSecretAsync(secretProperties.Name);
                        result[secretProperties.Name] = secret.Value?.Value ?? string.Empty;
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                    {
                        _logger.Warning("Secret '{SecretName}' was listed but not found when retrieving.", secretProperties.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error retrieving secret '{SecretName}'", secretProperties.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list or retrieve secrets from Azure Key Vault.");
                throw;
            }

            return result;
        }

    }
}
