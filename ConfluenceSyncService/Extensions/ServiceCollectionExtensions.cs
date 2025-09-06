using ConfluenceSyncService.Common.Constants;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Endpoints;
using ConfluenceSyncService.Hosted;
using ConfluenceSyncService.Identity;
using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Links;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Options;
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
using Serilog;

namespace ConfluenceSyncService.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration config)
        {
            Log.Information("Starting AddAppServices...");

            // Tiny-app switch: when true, only endpoints (ACK/ping), no background workers.
            var ackOnly = config.GetValue<bool>("Hosting:AckOnly");
            Log.Information("AckOnly mode: {AckOnly}", ackOnly);

            #region Core Configuration
            Log.Information("Configuring HttpClients...");
            // Default client for anything not using the named ones
            services.AddHttpClient();

            // Named HttpClient for Microsoft Graph. BaseUrl can be set via:
            services.AddHttpClient("graph", (sp, c) =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var baseUrl = cfg["Graph:BaseUrl"] ?? "https://graph.microsoft.com/";
                if (!baseUrl.EndsWith("/")) baseUrl += "/";
                c.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
            });
            Log.Information("HttpClients configured");
            #endregion

            #region Options (binds)
            Log.Information("Configuring options...");
            services.AddOptions<ClickerIdentityOptions>().BindConfiguration("Identity");

            // Policy knobs for link behavior (lives in ConfluenceSyncService.Options)
            services.AddOptions<AckLinkOptions>()
                .BindConfiguration("AckLink")
                .Validate(o => o.Policy != null
                            && o.Policy.InitialTtlCapHours > 0
                            && o.Policy.ChaserTtlHours > 0,
                          "AckLink.Policy invalid (cap/chaser TTLs must be > 0)")
                .ValidateOnStart();

            // Signer config (lives in ConfluenceSyncService.Security)
            // Prefer "AckLink:Signer", but fall back to "AckLink" if you haven't split config yet.
            var signerSection = config.GetSection("AckLink:Signer");
            if (!signerSection.Exists()) signerSection = config.GetSection("AckLink");

            services.AddOptions<ConfluenceSyncService.Security.AckSignerOptions>()
                .Bind(signerSection)
                .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey),
                          "AckLink:Signer:SigningKey is required (or AckLink:SigningKey if using fallback).")
                .ValidateOnStart();

            // C2: bind ChaserJob options (use the local 'config', not 'Configuration')
            services.AddOptions<ChaserJobOptions>()
                .BindConfiguration("ChaserJob")
                .Validate(o => o.CadenceMinutes > 0 && o.BatchSize > 0, "ChaserJob cadence/batch must be > 0")
                .Validate(o => o.BusinessWindow is not null, "ChaserJob.BusinessWindow required")
                .ValidateOnStart();

            // C2: background job (self-disables if Enabled=false)
            services.AddHostedService<ConfluenceSyncService.Scheduler.ChaserJobHostedService>();

            services.AddOptions<RegionOffsetsOptions>().BindConfiguration("RegionOffsets");
            services.AddOptions<ConfluenceSyncService.Options.TeamsOptions>()
                .BindConfiguration("Teams");
            services.AddOptions<ChaserOptions>().BindConfiguration("Chaser");
            services.AddOptions<SharePointFieldMappingsOptions>().BindConfiguration("SharePointFieldMappings");
            Log.Information("Options configured");
            #endregion

            #region MS Graph Integration

            Log.Information("Configuring MS Graph integration...");
            services.AddSingleton<ConfidentialClientApp>();
            services.AddSingleton<IMsalHttpClientFactory, MsalHttpClientFactory>();

            // HMAC signer for ACK link verification (secrets-backed; AKV or SQLite via ISecretsProvider)
            services.AddSingleton<ConfluenceSyncService.Security.IHmacSigner,
                                  ConfluenceSyncService.Security.SecretsBackedHmacSigner>();

            // App-only Graph token provider for Teams + chaser
            services.AddSingleton<ConfluenceSyncService.Teams.IGraphTokenProvider,
                                  ConfluenceSyncService.MSGraphAPI.GraphTokenProvider>();

            // Delegated token provider (for Teams)
            // In ServiceCollectionExtensions.cs
            services.AddSingleton<ITeamsGraphTokenProvider>(provider =>
            {
                var secrets = provider.GetRequiredService<ISecretsProvider>();
                var logger = provider.GetRequiredService<Serilog.ILogger>();
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient();
                return new TeamsGraphTokenProvider(secrets, logger, httpClient);
            });

            Log.Information("MS Graph integration configured");

            #endregion

            #region Business Services and Internal API
            Log.Information("Configuring business services...");
            services.AddSingleton<StartupLoaderService>();
            services.AddSingleton<IWorkflowMappingProvider, WorkflowMappingProvider>();
            services.AddScoped<ISyncOrchestratorService, SyncOrchestratorService>();

            services.AddSingleton<ITaskIdIssuer, SqliteTaskIdIssuer>();

            // FIXED: SignatureService registration - use lazy initialization instead of sync call
            services.AddSingleton<SignatureService>(sp =>
            {
                // This will be called when the service is first requested, not during container building
                Log.Information("Initializing SignatureService...");
                var secrets = sp.GetRequiredService<ISecretsProvider>();

                try
                {
                    var b64 = secrets.GetApiKeyAsync(SecretsKeys.LinkSigningKey)
                                     .GetAwaiter().GetResult();

                    if (string.IsNullOrWhiteSpace(b64))
                    {
                        Log.Error("Secret '{SecretKey}' is missing or empty", SecretsKeys.LinkSigningKey);
                        throw new InvalidOperationException($"Secret '{SecretsKeys.LinkSigningKey}' is missing.");
                    }

                    Log.Information("SignatureService initialized successfully");
                    return new SignatureService(b64);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to initialize SignatureService");
                    throw;
                }
            });

            // Identity + due-date helpers + signed link generator
            services.AddSingleton<IClickerIdentityProvider, ClickerIdentityProvider>();
            services.AddSingleton<IRegionDueCalculator, RegionDueCalculator>();
            services.AddSingleton<ISignedLinkGenerator, SignedLinkGenerator>();
            Log.Information("Business services configured");
            #endregion

            #region SharePoint/Teams/Email Clients
            Log.Information("Configuring SharePoint/Teams/Email clients...");
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
            Log.Information("SharePoint/Teams/Email clients configured");
            #endregion

            #region Confluence and External Clients
            Log.Information("Configuring Confluence client...");
            // Confluence REST client (unchanged)
            services.AddHttpClient<ConfluenceClient>()
                .AddTypedClient((httpClient, provider) =>
                {
                    var configuration = provider.GetRequiredService<IConfiguration>();
                    var secretsProvider = provider.GetRequiredService<ISecretsProvider>();
                    return new ConfluenceClient(httpClient, configuration, secretsProvider);
                });

            services.AddSingleton<ICursorStore, FileCursorStore>();
            Log.Information("Confluence client configured");
            #endregion

            #region Entity Framework / DB
            Log.Information("Configuring Entity Framework...");
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

                Log.Verbose("Using database connection: {ConnectionString}", cs);
                options.UseSqlite(cs);
            });
            Log.Information("Entity Framework configured");
            #endregion

            #region Worker and Hosted Services
            Log.Information("Configuring worker and hosted services...");
            // Register Worker for management API access either way
            services.AddSingleton<Worker>();
            services.AddSingleton<IWorkerService>(provider => provider.GetRequiredService<Worker>());

            if (!ackOnly)
            {
                Log.Information("Full mode: Adding background services");
                // Full mode (VM): run background services
                services.AddHostedService(provider => provider.GetRequiredService<Worker>());
                services.AddHostedService<ChaserService>();
            }
            else
            {
                Log.Information("ACK-only mode: Skipping background services");
            }

            // ACK handler (for minimal API endpoint) – always available
            services.AddTransient<AckActionHandler>();
            Log.Information("Worker and hosted services configured");


            Log.Information("AddAppServices completed successfully");
            return services;
            #endregion
        }

        public static IServiceCollection AddAppSecrets(this IServiceCollection services, IConfiguration configuration)
        {
            Log.Information("Starting AddAppSecrets...");

            string secretsProviderType = configuration["SecretsProvider:Type"] ?? "Sqlite";
            Log.Information("Using secrets provider type: {ProviderType}", secretsProviderType);

            switch (secretsProviderType)
            {
                case "AzureKeyVault":
                    Log.Information("Configuring Azure Key Vault secrets provider");
                    services.AddSingleton<ISecretsProvider>(provider =>
                        new AzureKeyVaultSecretsProvider(configuration));
                    break;

                case "Sqlite":
                    Log.Information("Configuring SQLite secrets provider");
                    // Register as Singleton - uses IServiceProvider to create scopes as needed
                    services.AddSingleton<SqliteSecretsProvider>();
                    services.AddSingleton<ISecretsProvider>(provider =>
                        provider.GetRequiredService<SqliteSecretsProvider>());
                    break;

                default:
                    Log.Error("Unsupported SecretsProvider type: {ProviderType}", secretsProviderType);
                    throw new InvalidOperationException($"Unsupported SecretsProvider type: {secretsProviderType}");
            }

            Log.Information("AddAppSecrets completed successfully");
            return services;
        }
    }
}
