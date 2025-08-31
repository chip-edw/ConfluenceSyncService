using ConfluenceSyncService.Common.Constants;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Endpoints;
using ConfluenceSyncService.Hosted;
using ConfluenceSyncService.Identity;
using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Links;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Security;
using ConfluenceSyncService.Services;
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Services.Maintenance;
using ConfluenceSyncService.Services.Secrets;
using ConfluenceSyncService.Services.State;
using ConfluenceSyncService.Services.Sync;
using ConfluenceSyncService.Services.Workflow;
using ConfluenceSyncService.SharePoint;
using ConfluenceSyncService.Teams;
using ConfluenceSyncService.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace ConfluenceSyncService.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration config)
        {
            // Tiny-app switch: when true, only endpoints (ACK/ping), no background workers.
            var ackOnly = config.GetValue<bool>("Hosting:AckOnly");

            #region Core Configuration
            // Default client for anything not using the named ones
            services.AddHttpClient();

            // Named HttpClient for Microsoft Graph. BaseUrl can be set via:
            //   appsettings:  "Graph": { "BaseUrl": "https://graph.microsoft.com/" }
            //   env var:      Graph__BaseUrl = https://graph.microsoft.com/
            services.AddHttpClient("graph", (sp, c) =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var baseUrl = cfg["Graph:BaseUrl"] ?? "https://graph.microsoft.com/";
                if (!baseUrl.EndsWith("/")) baseUrl += "/";
                c.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
            });
            #endregion

            #region Options (binds)
            services.AddOptions<ClickerIdentityOptions>().BindConfiguration("Identity");
            services.AddOptions<AckLinkOptions>().BindConfiguration("AckLink");
            services.AddOptions<RegionOffsetsOptions>().BindConfiguration("RegionOffsets");
            services.AddOptions<TeamsOptions>().BindConfiguration("Teams");
            services.AddOptions<ChaserOptions>().BindConfiguration("Chaser");
            services.AddOptions<SharePointFieldMappingsOptions>().BindConfiguration("SharePointFieldMappings");
            #endregion

            #region MS Graph Integration
            services.AddSingleton<ConfidentialClientApp>();
            services.AddSingleton<IMsalHttpClientFactory, MsalHttpClientFactory>();

            // HMAC signer for ACK link verification (secrets-backed; AKV or SQLite via ISecretsProvider)
            services.AddSingleton<ConfluenceSyncService.Security.IHmacSigner,
                                  ConfluenceSyncService.Security.SecretsBackedHmacSigner>();

            // App-only Graph token provider for Teams + chaser
            services.AddSingleton<ConfluenceSyncService.Teams.IGraphTokenProvider,
                                  ConfluenceSyncService.MSGraphAPI.GraphTokenProvider>();
            #endregion

            #region Business Services and Internal API
            services.AddSingleton<StartupLoaderService>();
            services.AddSingleton<IWorkflowMappingProvider, WorkflowMappingProvider>();
            services.AddScoped<ISyncOrchestratorService, SyncOrchestratorService>();

            // SignatureService: inject base64 key once via DI
            services.AddSingleton<SignatureService>(sp =>
            {
                var secrets = sp.GetRequiredService<ISecretsProvider>();
                var b64 = secrets.GetApiKeyAsync(SecretsKeys.LinkSigningKey)
                                 .GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(b64))
                    throw new InvalidOperationException($"Secret '{SecretsKeys.LinkSigningKey}' is missing.");

                return new SignatureService(b64);
            });

            // Identity + due-date helpers + signed link generator
            services.AddSingleton<IClickerIdentityProvider, ClickerIdentityProvider>();
            services.AddSingleton<IRegionDueCalculator, RegionDueCalculator>();
            services.AddSingleton<ISignedLinkGenerator, SignedLinkGenerator>();

            // SharePoint/Teams/Email clients now use the named "graph" HttpClient so relative paths resolve correctly
            services.AddTransient<SharePointClient>(provider =>
            {
                var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
                var confidentialClient = provider.GetRequiredService<ConfidentialClientApp>();
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new SharePointClient(httpClient, confidentialClient, configuration);
            });

            services.AddTransient<TeamsClient>(provider =>
            {
                var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
                var confidentialClient = provider.GetRequiredService<ConfidentialClientApp>();
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new TeamsClient(httpClient, confidentialClient, configuration);
            });

            services.AddTransient<EmailClient>(provider =>
            {
                var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
                var confidentialClient = provider.GetRequiredService<ConfidentialClientApp>();
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new EmailClient(httpClient, confidentialClient, configuration);
            });

            // FIXED: Register SharePointTaskUpdater to use the "graph" named HttpClient with authentication
            services.AddTransient<SharePointTaskUpdater>(provider =>
            {
                var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
                var fieldMappingOptions = provider.GetRequiredService<IOptions<SharePointFieldMappingsOptions>>();
                var confidentialClientApp = provider.GetRequiredService<ConfidentialClientApp>();
                var configuration = provider.GetRequiredService<IConfiguration>();
                return new SharePointTaskUpdater(httpClient, fieldMappingOptions, confidentialClientApp, configuration);
            });

            // Map the interface to the same instance
            services.AddTransient<ISharePointTaskUpdater>(provider =>
                provider.GetRequiredService<SharePointTaskUpdater>());

            // Notifications
            services.AddSingleton<INotificationService, TeamsNotificationService>();

            // Confluence REST client (unchanged)
            services.AddHttpClient<ConfluenceClient>()
                .AddTypedClient((httpClient, provider) =>
                {
                    var configuration = provider.GetRequiredService<IConfiguration>();
                    var secretsProvider = provider.GetRequiredService<ISecretsProvider>();
                    return new ConfluenceClient(httpClient, configuration, secretsProvider);
                });

            services.AddSingleton<ICursorStore, FileCursorStore>();
            #endregion

            #region Entity Framework / DB
            services.AddDbContext<ApplicationDbContext>((sp, options) =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var env = sp.GetRequiredService<IHostEnvironment>();

                // 1) Try appsettings/ENV: ConnectionStrings__DefaultConnection
                var cs = cfg.GetConnectionString("DefaultConnection");

                // 2) Fallback to the packaged DB under ./DB if nothing was provided
                if (string.IsNullOrWhiteSpace(cs))
                {
                    var fallbackPath = Path.Combine(env.ContentRootPath, "DB", "ConfluenceSyncServiceDB.db");
                    cs = $"Data Source={fallbackPath};Cache=Shared";
                }

                options.UseSqlite(cs);
            });
            #endregion

            #region Worker and Hosted Services
            // Register Worker for management API access either way
            services.AddSingleton<Worker>();
            services.AddSingleton<IWorkerService>(provider => provider.GetRequiredService<Worker>());

            if (!ackOnly)
            {
                // Full mode (VM): run background services
                services.AddHostedService(provider => provider.GetRequiredService<Worker>());
                services.AddHostedService<ChaserService>();
            }

            // ACK handler (for minimal API endpoint) — always available
            services.AddTransient<AckActionHandler>();
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
