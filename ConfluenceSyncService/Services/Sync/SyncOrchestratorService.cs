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
            _logger.Information("Starting synchronization process between SharePoint and Confluence...");

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
                    _logger.Information("Processing SharePoint site: {SitePath}", site.SitePath);

                    foreach (var list in site.Lists)
                    {
                        _logger.Information("Processing SharePoint list: {ListDisplayName}", list.DisplayName);

                        var sharePointItems = await _sharePointClient.GetAllListItemsAsync(site.SitePath, list.DisplayName);
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
