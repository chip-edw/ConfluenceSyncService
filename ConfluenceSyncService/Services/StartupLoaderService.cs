using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.Models.Configuration;
using Serilog;

namespace ConfluenceSyncService.Services
{
    public class StartupLoaderService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ISecretsProvider _secretsProvider;

        public StartupLoaderService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ISecretsProvider secretsProvider)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _secretsProvider = secretsProvider;
        }

        public async Task LoadAllStartupDataAsync()
        {
            Log.Information("Starting initial load of all startup configuration data...");

            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Load SharePoint Site/List config from appsettings.json
                StartupConfiguration.LoadSharePointConfiguration(_configuration);
            }

            // Initialize in-memory secret cache if supported
            if (_secretsProvider is IInitializableSecretsProvider initializable)
            {
                await initializable.InitializeAsync();
            }




            Log.Information("Startup data loaded successfully.");
        }
    }

    public static class StartupConfiguration
    {
        public static List<SharePointSiteConfig> SharePointSites { get; set; } = new();

        public static void LoadSharePointConfiguration(IConfiguration configuration)
        {
            var sharePointSection = configuration.GetSection("SharePoint");
            var settings = sharePointSection.Get<SharePointSettings>();

            if (settings == null || settings.Sites == null || settings.Sites.Count == 0)
            {
                Log.Warning("No SharePoint sites found in appsettings.json.");
                SharePointSites = new List<SharePointSiteConfig>();
                return;
            }

            SharePointSites = settings.Sites;
            Log.Information("Loaded {Count} SharePoint sites from configuration.", SharePointSites.Count);
        }
    }
}
