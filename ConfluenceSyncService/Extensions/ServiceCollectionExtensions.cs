using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Services;
using ConfluenceSyncService.Services.Clients;
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
                options.UseSqlite("Data Source=ConfluenceSyncServiceDB.db"));
            #endregion

            #region Worker and Hosted Services
            services.AddScoped<IWorkerService, Worker>();
            services.AddHostedService<ScopedWorkerHostedService>();
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
                    // Register as Singleton to match the lifetime requirements
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