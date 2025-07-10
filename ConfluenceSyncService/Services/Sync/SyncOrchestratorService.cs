using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Utilities;

using Serilog;

namespace ConfluenceSyncService.Services.Sync
{
    public class SyncOrchestratorService : ISyncOrchestratorService
    {
        private readonly SharePointClient _sharePointClient;
        private readonly ConfluenceClient _confluenceClient;
        private readonly Serilog.ILogger _logger;

        public SyncOrchestratorService(SharePointClient sharePointClient, ConfluenceClient confluenceClient)
        {
            _sharePointClient = sharePointClient;
            _confluenceClient = confluenceClient;
            _logger = Log.ForContext<SyncOrchestratorService>();
        }

        public async Task RunSyncAsync(CancellationToken cancellationToken)
        {
            _logger.Information("Starting synchronization process between SharePoint and Confluence...\n\n\n");

            try
            {
                var sites = StartupConfiguration.SharePointSites;

                if (sites == null || sites.Count == 0)
                {
                    _logger.Warning("No SharePoint sites configured in StartupConfiguration.");
                    return;
                }

                foreach (var site in sites)
                {
                    _logger.Information("Processing SharePoint site: {SiteId}", site.SiteId);

                    foreach (var list in site.Lists)
                    {
                        _logger.Information("Processing SharePoint list: {ListDisplayName}", list.DisplayName);

                        //  TEMP: Test delta sync logic
                        var testSince = DateTime.UtcNow.AddDays(-7); // adjust to match your data
                        var deltaItems = await _sharePointClient.GetRecentlyModifiedItemsAsync(site.SiteId, list.DisplayName, testSince);

                        _logger.Information("Delta test: Found {Count} recently modified SharePoint items since {Since}", deltaItems.Count, testSince);

                        foreach (var deltaItem in deltaItems)
                        {
                            _logger.Information("Delta Item: ID={Id}, Title={Title}, Modified={Modified}",
                                deltaItem.Id, deltaItem.Title, deltaItem.LastModifiedUtc);
                        }
                        // END TEMP:

                        var sharePointItems = await _sharePointClient.GetAllListItemsAsync(site.SiteId, list.DisplayName);
                        _logger.Information("Loaded {Count} items from SharePoint list: {List}", sharePointItems.Count, list.DisplayName);

                        var confluenceItems = await _confluenceClient.GetAllDatabaseItemsAsync(list.ConfluenceDatabaseId);
                        _logger.Information("Loaded {Count} items from Confluence database: {DatabaseId}", confluenceItems.Count, list.ConfluenceDatabaseId);

                        foreach (var spItem in sharePointItems)
                        {
                            if (!confluenceItems.Any(ci => ci.ExternalId == spItem.Id))
                            {
                                _logger.Information("New item detected in SharePoint list {List}. Syncing to Confluence DB {DbId}: {ItemId}",
                                    list.DisplayName, list.ConfluenceDatabaseId, spItem.Id);

                                var confluenceRow = SyncMapper.MapToConfluenceRow(spItem);
                                await _confluenceClient.CreateDatabaseItemAsync(confluenceRow, list.ConfluenceDatabaseId);

                            }
                        }
                    }
                }

                _logger.Information("Synchronization process completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An error occurred during synchronization.");
            }
        }
    }
}