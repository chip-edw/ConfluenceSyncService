using ConfluenceSyncService.Models;
using Serilog;

namespace ConfluenceSyncService.Services
{
    public class StartupLoaderService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public StartupLoaderService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;

        }

        public async Task LoadAllStartupDataAsync()
        {
            Log.Information("Starting initial load of all startup configuration data...");

            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Critical! Configurations go here


            }

            Log.Information("Startup data loaded successfully.");

        }
    }
}
