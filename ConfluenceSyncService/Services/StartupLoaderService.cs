using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Models;
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

                // Load Teams config from appsettings.json
                StartupConfiguration.LoadTeamsConfiguration(_configuration);

                // Load Email config from appsettings.json
                StartupConfiguration.LoadEmailConfiguration(_configuration);
            }

            // Initialize in-memory secret cache if supported
            if (_secretsProvider is IInitializableSecretsProvider initializable)
            {
                await initializable.InitializeAsync();
            }

            Log.Information("Startup data loaded successfully.");
        }
    }
}