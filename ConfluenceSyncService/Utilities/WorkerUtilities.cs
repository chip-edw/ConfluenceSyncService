using ConfluenceSyncService.Services.Clients;
using Serilog;

namespace ConfluenceSyncService.Utilities
{
    public class WorkerUtilities
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly Serilog.ILogger _logger;

        public WorkerUtilities(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = Log.ForContext<WorkerUtilities>();
        }

        public string GetOperatingSystem()
        {
            return StartupConfiguration.DetermineOS();
        }

        public async Task ListSharePointFieldNamesAsync(string siteId, string listName)
        {
            try
            {
                Console.WriteLine("\n\n");
                _logger.Information("=== DISCOVERING SHAREPOINT FIELD NAMES ===");

                using var scope = _serviceScopeFactory.CreateScope();
                var sharePointClient = scope.ServiceProvider.GetRequiredService<SharePointClient>();

                var fieldMap = await sharePointClient.GetListFieldsAsync(siteId, listName);

                Console.WriteLine("SharePoint Field Mappings:");
                foreach (var field in fieldMap)
                {
                    Console.WriteLine($"Display: '{field.Key}' -> Internal: '{field.Value}'");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get SharePoint field names");
            }
            _logger.Information("=== END SHAREPOINT FIELD NAMES DISCOVERY ===");
        }

        public async Task<bool> CreateTransitionTrackerTableAsync(string pageId, string templateName)
        {
            try
            {
                Console.WriteLine("\n\n");
                _logger.Information("=== TESTING NEW TABLE CREATION ===");

                using var scope = _serviceScopeFactory.CreateScope();
                var confluenceClient = scope.ServiceProvider.GetRequiredService<ConfluenceClient>();

                var createSuccess = await confluenceClient.CreateTransitionTrackerTableAsync(pageId, templateName);
                Console.WriteLine($"Table creation successful: {createSuccess}");

                _logger.Information("=== END TABLE CREATION TEST ===");
                return createSuccess;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create new table");
                _logger.Information("=== END TABLE CREATION TEST ===");
                return false;
            }
        }

        public async Task<Dictionary<string, string>?> UpdateStatusAndParseTableAsync(string pageId)
        {
            try
            {
                Console.WriteLine("\n\n");
                _logger.Information("=== CONFLUENCE STATUS TEXT UPDATE AND PARSING ===");

                using var scope = _serviceScopeFactory.CreateScope();
                var confluenceClient = scope.ServiceProvider.GetRequiredService<ConfluenceClient>();

                // First, update any status text based on current colors
                var updateSuccess = await confluenceClient.UpdateStatusTextBasedOnColorAsync(pageId);
                Console.WriteLine($"Status text update successful: {updateSuccess}");

                // Then parse the table data
                var tableData = await confluenceClient.ParseTransitionTrackerTableAsync(pageId);

                Console.WriteLine("=== PARSED TABLE DATA ===");
                foreach (var kvp in tableData)
                {
                    Console.WriteLine($"{kvp.Key}: {kvp.Value}");
                }

                _logger.Information("=== END STATUS UPDATE AND PARSING TEST ===");
                return tableData;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to test status update and parsing");
                _logger.Information("=== END STATUS UPDATE AND PARSING TEST ===");
                return null;
            }
        }
    }
}