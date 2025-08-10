using ConfluenceSyncService.Common.Constants;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Services;
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Services.Maintenance;
using ConfluenceSyncService.Services.Secrets;
using ConfluenceSyncService.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;

namespace ConfluenceSyncService.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAppServices(this IServiceCollection services)
        {
            #region Core Configuration
            services.AddHttpClient();
            #endregion

            #region MS Graph Integration
            services.AddSingleton<ConfidentialClientApp>();
            services.AddSingleton<IMsalHttpClientFactory, MsalHttpClientFactory>();
            #endregion

            #region Business Services and Internal API
            services.AddSingleton<StartupLoaderService>();
            services.AddScoped<ISyncOrchestratorService, SyncOrchestratorService>();

            // SignatureService: inject base64 key once via DI (Option B)
            services.AddSingleton<SignatureService>(sp =>
            {
                var secrets = sp.GetRequiredService<ISecretsProvider>();
                // Secrets must already be initialized by the hosted initializer below
                var b64 = secrets.GetApiKeyAsync(SecretsKeys.LinkSigningKey)
                                 .GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(b64))
                    throw new InvalidOperationException($"Secret '{SecretsKeys.LinkSigningKey}' is missing.");

                return new SignatureService(b64);
            });

            services.AddTransient<SharePointClient>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient();
                var confidentialClient = provider.GetRequiredService<ConfidentialClientApp>();
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new SharePointClient(httpClient, confidentialClient, configuration);
            });

            services.AddHttpClient<ConfluenceClient>()
                .AddTypedClient((httpClient, provider) =>
                {
                    var configuration = provider.GetRequiredService<IConfiguration>();
                    var secretsProvider = provider.GetRequiredService<ISecretsProvider>();
                    return new ConfluenceClient(httpClient, configuration, secretsProvider);
                });
            #endregion

            #region Entity Framework / DB
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite("Data Source=ConfluenceSyncServiceDB.db"), ServiceLifetime.Scoped);
            #endregion

            #region Worker and Hosted Services
            // Register Worker as both IWorkerService (for Management API) and as HostedService
            services.AddSingleton<Worker>();
            services.AddSingleton<IWorkerService>(provider => provider.GetRequiredService<Worker>());
            services.AddHostedService<Worker>(provider => provider.GetRequiredService<Worker>());
            #endregion

            return services;
        }

        public static IServiceCollection AddAppSecrets(this IServiceCollection services, IConfiguration configuration)
        {
            string secretsProviderType = configuration["SecretsProvider:Type"] ?? "Sqlite";

            switch (secretsProviderType)
            {
                case "AzureKeyVault":
                    services.AddSingleton<ISecretsProvider>(provider =>
                        new AzureKeyVaultSecretsProvider(configuration));
                    break;

                case "Sqlite":
                    // Register as Singleton - uses IServiceProvider to create scopes as needed
                    services.AddSingleton<SqliteSecretsProvider>();
                    services.AddSingleton<ISecretsProvider>(provider =>
                        provider.GetRequiredService<SqliteSecretsProvider>());
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported SecretsProvider type: {secretsProviderType}");
            }

            return services;
        }
    }
}