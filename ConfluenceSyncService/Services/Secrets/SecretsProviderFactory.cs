using ConfluenceSyncService.Common.Secrets;
using Serilog;

namespace ConfluenceSyncService.Services.Secrets
{
    public static class SecretsProviderFactory
    {
        public static ISecretsProvider Create(IConfiguration configuration, IServiceProvider provider)
        {
            var providerType = configuration["SecretsProvider:Type"];
            Log.Information($"Using Secrets Provider: {providerType}");


            if (string.IsNullOrWhiteSpace(providerType))
                throw new InvalidOperationException("SecretsProvider:Type is not defined in configuration.");

            switch (providerType.Trim().ToLowerInvariant())
            {
                case "azurekeyvault":
                    return provider.GetRequiredService<AzureKeyVaultSecretsProvider>();

                case "sqlite":
                    return provider.GetRequiredService<SqliteSecretsProvider>();

                default:
                    throw new InvalidOperationException($"Unknown SecretsProvider type: '{providerType}'");
            }
        }
    }
}
