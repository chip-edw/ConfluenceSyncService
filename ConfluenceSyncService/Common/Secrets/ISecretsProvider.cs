namespace ConfluenceSyncService.Common.Secrets
{
    public interface ISecretsProvider
    {
        Task<string?> GetApiKeyAsync(string keyName);
        Task<Dictionary<string, string>> GetAllApiKeysAsync();

        Task SaveRefreshTokenAsync(string keyName, string newRefreshToken);
    }
}
