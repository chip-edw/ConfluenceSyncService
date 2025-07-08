using ConfluenceSyncService.Models;
using Microsoft.EntityFrameworkCore;

namespace ConfluenceSyncService.Common.Secrets
{
    public class SqliteSecretsProvider : ISecretsProvider
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<SqliteSecretsProvider> _logger;

        public SqliteSecretsProvider(ApplicationDbContext dbContext, ILogger<SqliteSecretsProvider> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task<string?> GetApiKeyAsync(string keyName)
        {
            _logger.LogInformation("Fetching API key from ConfigStore: {KeyName}", keyName);

            var entry = await _dbContext.ConfigStore
                .Where(c => c.ValueName == keyName)
                .Select(c => c.Value)
                .FirstOrDefaultAsync();

            if (entry == null)
                _logger.LogWarning("No value found in ConfigStore for key: {KeyName}", keyName);
            else
                _logger.LogInformation("Value retrieved for key '{KeyName}': {Length} characters", keyName, entry.Length);

            return entry;
        }


        public async Task<Dictionary<string, string>> GetAllApiKeysAsync()
        {
            return await _dbContext.ConfigStore
                .ToDictionaryAsync(c => c.ValueName, c => c.Value);
        }

        public async Task SaveRefreshTokenAsync(string keyName, string newRefreshToken)
        {
            var entry = await _dbContext.ConfigStore
                .FirstOrDefaultAsync(c => c.ValueName == keyName);

            if (entry != null)
            {
                _logger.LogInformation("Found existing entry for '{KeyName}'. Current value length: {Length}", keyName, entry.Value?.Length ?? 0);
                _logger.LogInformation("Before update - UpdatedAt: {UpdatedAt}", entry.UpdatedAt);

                // Check if the value is actually different
                if (entry.Value == newRefreshToken)
                {
                    _logger.LogWarning("New refresh token is identical to existing token - no update needed");
                    return;
                }

                entry.Value = newRefreshToken;

                // Check entity state before save
                var entityEntry = _dbContext.Entry(entry);
                _logger.LogInformation("Entity state before SaveChanges: {State}", entityEntry.State);
                _logger.LogInformation("Entity has changes: {HasChanges}", entityEntry.Properties.Any(p => p.IsModified));

                _logger.LogInformation("Updating existing refresh token for key '{KeyName}'", keyName);
            }
            else
            {
                var newEntry = new ConfigStore
                {
                    ValueName = keyName,
                    Value = newRefreshToken,
                    ValueType = "RefreshToken",
                    Description = $"Refresh token for {keyName}"
                };
                _dbContext.ConfigStore.Add(newEntry);
                _logger.LogInformation("Creating new refresh token entry for key '{KeyName}'", keyName);
            }

            var changeCount = await _dbContext.SaveChangesAsync();
            _logger.LogInformation("SaveChanges completed. Changes saved: {ChangeCount}", changeCount);

            // Check the value after save
            var updatedEntry = await _dbContext.ConfigStore.FirstOrDefaultAsync(c => c.ValueName == keyName);
            if (updatedEntry != null)
            {
                _logger.LogInformation("After save - UpdatedAt: {UpdatedAt}", updatedEntry.UpdatedAt);
            }
        }
    }
}
