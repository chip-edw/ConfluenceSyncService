using ConfluenceSyncService.Auth;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.ConfluenceAPI;
using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Services;
using ConfluenceSyncService.Services.Clients;
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
            services.AddScoped<ISecretsProvider, SqliteSecretsProvider>();
            #endregion

            #region MS Graph Integration
            services.AddSingleton<ConfidentialClientApp>();
            services.AddSingleton<IMsalHttpClientFactory, MsalHttpClientFactory>();
            #endregion

            #region Business Services and Internal API
            services.AddSingleton<StartupLoaderService>();
            services.AddScoped<IConfluenceAuthClient, ConfluenceAuthClient>();
            services.AddScoped<ConfluenceTokenManager>();
            services.AddScoped<ISyncOrchestratorService, SyncOrchestratorService>();

            services.AddTransient<SharePointClient>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient();
                var confidentialClient = provider.GetRequiredService<ConfidentialClientApp>();
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new SharePointClient(httpClient, confidentialClient, configuration);
            });



            services.AddHttpClient<ConfluenceClient>();

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
