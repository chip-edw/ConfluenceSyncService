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

            // Register individual secrets providers
            services.AddScoped<SqliteSecretsProvider>();
            services.AddScoped<AzureKeyVaultSecretsProvider>();

            // Dynamically bind ISecretsProvider based on appsettings.json
            services.AddScoped<ISecretsProvider>(provider =>
                SecretsProviderFactory.Create(
                    provider.GetRequiredService<IConfiguration>(),
                    provider));
            #endregion


            #region MS Graph Integration
            services.AddSingleton<ConfidentialClientApp>();
            services.AddSingleton<IMsalHttpClientFactory, MsalHttpClientFactory>();
            #endregion

            #region Business Services and Internal API
            services.AddSingleton<StartupLoaderService>();
            //services.AddScoped<IConfluenceAuthClient, ConfluenceAuthClient>();
            services.AddScoped<ISyncOrchestratorService, SyncOrchestratorService>();

            services.AddTransient<SharePointClient>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient();
                var confidentialClient = provider.GetRequiredService<ConfidentialClientApp>();
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new SharePointClient(httpClient, confidentialClient, configuration);
            });


            services.AddHttpClient<ConfluenceClient>((provider, httpClient) =>
            {
                // Optional: set default headers here if needed
            }).AddTypedClient((httpClient, provider) =>
            {
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new ConfluenceClient(httpClient, configuration);
            });



            #endregion

            #region Entity Framework / DB
            //Register ApplicationDbContext needed so we can create new DbContext instances to use across threads
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite("Data Source=ConfluenceSyncServiceDB.db"));
            #endregion

            #region Worker and Hosted Services
            services.AddScoped<IWorkerService, Worker>();
            services.AddHostedService<ScopedWorkerHostedService>();
            #endregion

            return services;
        }
    }
}
