using System.Text.Json;

namespace ConfluenceSyncService.Common.Secrets
{
    public class LocalSecretsProvider : ISecretsProvider
    {
        private readonly ILogger<LocalSecretsProvider> _logger;
        private readonly Dictionary<string, string> _apiKeys;

        public LocalSecretsProvider(IConfiguration configuration, ILogger<LocalSecretsProvider> logger)
        {
            _logger = logger;
            var keysFilePath = configuration.GetValue<string>("KeysFilePath") ?? "keys.json";

            if (!File.Exists(keysFilePath))
            {
                _logger.LogWarning("Keys file {KeysFilePath} not found.", keysFilePath);
                _apiKeys = new Dictionary<string, string>();
                return;
            }

            var fileContent = File.ReadAllText(keysFilePath);
            var encryptedKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(fileContent) ?? new Dictionary<string, string>();

            _apiKeys = encryptedKeys.ToDictionary(
                kvp => kvp.Key,
                kvp => Decrypt(kvp.Value) // Decrypt as you load
            );
        }

        public Task<string?> GetApiKeyAsync(string keyName)
        {
            _apiKeys.TryGetValue(keyName, out var apiKey);
            return Task.FromResult(apiKey);
        }

        public Task<Dictionary<string, string>> GetAllApiKeysAsync()
        {
            return Task.FromResult(_apiKeys);
        }

        private string Decrypt(string encryptedText)
        {
            //  Placeholder simple decryptor -- replace with real AES later!
            // For now assume plain text for initial dev if you want.

            return encryptedText; // TODO: Implement real encryption later when ready.
        }
    }
}
