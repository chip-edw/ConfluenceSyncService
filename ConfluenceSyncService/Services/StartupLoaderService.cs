using ConfluenceSyncService.Models;
using Serilog;

namespace ConfluenceSyncService.Services
{
    public class StartupLoaderService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;

        public StartupLoaderService(IServiceScopeFactory scopeFactory, IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;

        }

        public async Task LoadAllStartupDataAsync()
        {
            Log.Information("Starting initial load of all startup configuration data...");

            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Critical! Configurations go here
                // Load SharePoint Site/List config from appsettings.json
                StartupConfiguration.LoadSharePointConfiguration(_configuration);


            }

            Log.Information("Startup data loaded successfully.");

        }
    }
}
