using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.Services.Clients;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ConfluenceSyncService.Services.Sync
{
    public class SyncOrchestratorService : ISyncOrchestratorService
    {
        private readonly SharePointClient _sharePointClient;
        private readonly ConfluenceClient _confluenceClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;

        public SyncOrchestratorService(
            SharePointClient sharePointClient,
            ConfluenceClient confluenceClient,
            ApplicationDbContext dbContext,
            IConfiguration configuration)
        {
            _sharePointClient = sharePointClient;
            _confluenceClient = confluenceClient;
            _dbContext = dbContext;
            _configuration = configuration;
            _logger = Log.ForContext<SyncOrchestratorService>();
        }

        public async Task RunSyncAsync(CancellationToken cancellationToken)
        {


            try
            {
                _logger.Information("=== STARTING TABLE SYNC WORKFLOW ===");
                // Step 1: Token management is handled in Worker, so we start with sync

                // Step 2: Sync all Confluence Status Text Updates
                await Step2_UpdateConfluenceStatusText(cancellationToken);

                // Step 3: Sync Confluence updates to SharePoint
                await Step3_SyncConfluenceToSharePoint(cancellationToken);

                // Step 4: Sync SharePoint updates to Confluence
                await Step4_SyncSharePointToConfluence(cancellationToken);

                _logger.Information("=== TABLE SYNC WORKFLOW COMPLETED SUCCESSFULLY ===");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in table sync workflow");
                throw;
            }
        }

        private async Task Step2_UpdateConfluenceStatusText(CancellationToken cancellationToken)
        {
            _logger.Information("=== STEP 2: CONFLUENCE STATUS TEXT UPDATES ===");

            var customerPages = await _confluenceClient.GetCustomerPagesAsync(cancellationToken);
            _logger.Information("Found {Count} customer pages", customerPages.Count);

            foreach (var page in customerPages)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    _logger.Debug("Processing page: {PageId} - {CustomerName}", page.Id, page.CustomerName);

                    // Check if page has transition tracker table
                    var fullPage = await _confluenceClient.GetPageWithContentAsync(page.Id, cancellationToken);

                    if (!PageHasTransitionTable(fullPage))
                    {
                        _logger.Information("Page {PageId} missing transition table, creating it", page.Id);
                        await _confluenceClient.CreateTransitionTrackerTableAsync(page.Id, page.CustomerName, cancellationToken);
                        continue;
                    }

                    // Update status text based on colors
                    var updateSuccess = await _confluenceClient.UpdateStatusTextBasedOnColorAsync(page.Id, cancellationToken);
                    _logger.Debug("Status text update for page {PageId}: {Success}", page.Id, updateSuccess);

                    if (updateSuccess)
                    {
                        // Parse the updated table data
                        var tableData = await _confluenceClient.ParseTransitionTrackerTableAsync(page.Id, cancellationToken);
                        _logger.Debug("Parsed {Count} fields from page {PageId}", tableData.Count, page.Id);

                        foreach (var kvp in tableData)
                        {
                            _logger.Debug("{FieldName}: {Value}", kvp.Key, kvp.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to update status text for page {PageId}", page.Id);
                    // Continue with other pages
                }
            }
        }

        private async Task Step3_SyncConfluenceToSharePoint(CancellationToken cancellationToken)
        {
            _logger.Information("=== STEP 3: SYNC CONFLUENCE TO SHAREPOINT ===");

            var customerPages = await _confluenceClient.GetCustomerPagesAsync(cancellationToken);
            var sites = StartupConfiguration.SharePointSites;
            var transitionTrackerList = sites?.FirstOrDefault()?.Lists?.FirstOrDefault(l => l.DisplayName == "Transition Tracker");

            if (transitionTrackerList == null)
            {
                _logger.Error("Transition Tracker SharePoint list not configured");
                return;
            }

            var siteId = sites.First().SiteId;

            foreach (var page in customerPages)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // Get full page content and parse table
                    var fullPage = await _confluenceClient.GetPageWithContentAsync(page.Id, cancellationToken);

                    if (!PageHasTransitionTable(fullPage))
                    {
                        _logger.Warning("Page {PageId} has no transition table, skipping", page.Id);
                        continue;
                    }

                    var tableData = await _confluenceClient.ParseTransitionTrackerTableAsync(page.Id, cancellationToken);
                    var confluenceTableRow = MapToConfluenceTableRow(tableData, fullPage);

                    //Temporary Test Parsing and logging.
                    //confluenceTableRow.TestParsing();

                    // Check if SharePoint item exists
                    var syncState = await GetOrCreateSyncState(page.Id, confluenceTableRow.CustomerName);

                    bool shouldSync = await ShouldSyncToSharePoint(confluenceTableRow, syncState);

                    if (shouldSync)
                    {
                        _logger.Information("Syncing page {PageId} to SharePoint", page.Id);
                        await SyncToSharePoint(confluenceTableRow, syncState, siteId, transitionTrackerList.DisplayName);
                    }
                    else
                    {
                        _logger.Debug("Page {PageId} does not need SharePoint sync", page.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to sync page {PageId} to SharePoint", page.Id);
                    // Continue with other pages
                }
            }
        }

        private async Task Step4_SyncSharePointToConfluence(CancellationToken cancellationToken)
        {
            _logger.Information("=== STEP 4: SYNC SHAREPOINT TO CONFLUENCE ===");

            var sites = StartupConfiguration.SharePointSites;
            var transitionTrackerList = sites?.FirstOrDefault()?.Lists?.FirstOrDefault(l => l.DisplayName == "Transition Tracker");

            if (transitionTrackerList == null)
            {
                _logger.Error("Transition Tracker SharePoint list not configured");
                return;
            }

            var siteId = sites.First().SiteId;
            var sharePointItems = await _sharePointClient.GetAllListItemsAsync(siteId, transitionTrackerList.DisplayName);

            foreach (var spItem in sharePointItems)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var pageId = spItem.Fields.TryGetValue("ConfluencePageId", out var pageIdObj) ? pageIdObj?.ToString() : null;

                    // Handle missing Confluence pages - create them from template if SharePoint has Sync Tracker set to 'Yes'
                    if (string.IsNullOrEmpty(pageId))
                    {
                        // CHECK: Only create if SyncTracker is enabled
                        if (!ShouldSyncBasedOnSyncTracker(spItem))
                        {
                            _logger.Debug("SharePoint item {ItemId} has no ConfluencePageId but SyncTracker is not 'Yes', skipping auto-creation", spItem.Id);
                            continue;
                        }

                        var customerName = spItem.Fields.TryGetValue("Title", out var titleObj) ? titleObj?.ToString() : null;

                        if (!string.IsNullOrEmpty(customerName))
                        {
                            _logger.Information("SharePoint item {ItemId} has no ConfluencePageId and SyncTracker='Yes'. Creating new Confluence page for customer: {CustomerName}",
                                spItem.Id, customerName);

                            try
                            {
                                // Create new page from template
                                var newPageId = await _confluenceClient.CreateCustomerPageFromTemplateAsync(customerName, cancellationToken);

                                // Update SharePoint with the new page info
                                await UpdateSharePointWithNewPageInfo(spItem, newPageId, siteId, transitionTrackerList.DisplayName);

                                // Create sync state for new page
                                var newSyncState = await GetOrCreateSyncState(newPageId, customerName);

                                _logger.Information("Successfully created Confluence page {PageId} for customer {CustomerName} and updated SharePoint item {ItemId}",
                                    newPageId, customerName, spItem.Id);

                                // Continue to next item - let next sync cycle handle the data sync
                                continue;
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "Failed to create Confluence page for customer {CustomerName} from SharePoint item {ItemId}",
                                    customerName, spItem.Id);
                                continue;
                            }
                        }
                        else
                        {
                            _logger.Warning("SharePoint item {ItemId} has no ConfluencePageId and no Title (customer name), skipping", spItem.Id);
                            continue;
                        }
                    }

                    // EXISTING: Handle items that already have Confluence pages
                    var syncState = await GetSyncStateByPageId(pageId);
                    if (syncState == null)
                    {
                        _logger.Warning("No sync state found for page {PageId}, skipping", pageId);
                        continue;
                    }

                    bool shouldSync = await ShouldSyncToConfluence(spItem, syncState);

                    if (shouldSync)
                    {
                        _logger.Information("Syncing SharePoint item {ItemId} to Confluence page {PageId}", spItem.Id, pageId);
                        await SyncToConfluence(spItem, syncState);
                    }
                    else
                    {
                        _logger.Debug("SharePoint item {ItemId} does not need Confluence sync", spItem.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to process SharePoint item {ItemId}", spItem.Id);
                    // Continue with other items
                }
            }
        }

        #region Helper Methods

        private bool ShouldSyncBasedOnSyncTracker(SharePointListItem spItem)
        {
            if (spItem.Fields.TryGetValue("SyncTracker", out var syncTrackerValue))
            {
                // Handle different possible values
                var syncTracker = syncTrackerValue?.ToString()?.ToLowerInvariant();

                return syncTracker switch
                {
                    "true" => true,
                    "yes" => true,
                    "1" => true,
                    _ => false  // Default to false for null, empty, "false", "no", "0", etc.
                };
            }

            // If SyncTracker field doesn't exist, default to false (don't sync)
            return false;
        }

        private bool ShouldSyncBasedOnConfluenceSyncTracker(ConfluenceTableRow confluenceRow)
        {
            var syncTracker = confluenceRow.SyncTracker?.ToLowerInvariant();

            return syncTracker switch
            {
                "yes" => true,
                "true" => true,
                "1" => true,
                _ => false
            };
        }

        private bool PageHasTransitionTable(ConfluencePage page)
        {
            // Check if the page content contains a transition tracker table
            // This is a simplified check - you might want to make it more robust
            return !string.IsNullOrEmpty(page.AdfContent) &&
                   page.AdfContent.Contains("Transition Tracker");
        }

        private ConfluenceTableRow MapToConfluenceTableRow(Dictionary<string, string> tableData, ConfluencePage page)
        {
            return new ConfluenceTableRow
            {
                PageId = page.Id,
                CustomerName = page.CustomerName,
                PageUrl = page.WebUrl,
                LastModifiedUtc = page.UpdatedAt,
                PageVersion = page.Version,
                Region = tableData.GetValueOrDefault("Region", ""),
                StatusFF = tableData.GetValueOrDefault("Status FF", ""),
                StatusCust = tableData.GetValueOrDefault("Status Cust.", ""),
                Phase = tableData.GetValueOrDefault("Phase", ""),
                GoLiveDate = tableData.GetValueOrDefault("Go-Live Date (YYYY-MM-DD)", ""),
                SupportGoLiveDate = tableData.GetValueOrDefault("Support Go-Live Date (YYYY-MM-DD)", ""),
                SupportImpact = tableData.GetValueOrDefault("Support Impact", ""),
                SupportAccepted = tableData.GetValueOrDefault("Support Accepted", ""),
                Notes = tableData.GetValueOrDefault("Notes", ""),
                SyncTracker = tableData.GetValueOrDefault("Sync Tracker", "")
            };
        }

        private async Task<TableSyncState> GetOrCreateSyncState(string pageId, string customerName)
        {
            var syncState = await _dbContext.Set<TableSyncState>()
                .FirstOrDefaultAsync(s => s.ConfluencePageId == pageId);

            if (syncState == null)
            {
                _logger.Information("Creating new sync state for page {PageId}", pageId);
                syncState = new TableSyncState
                {
                    ConfluencePageId = pageId,
                    CustomerName = customerName
                };
                _dbContext.Set<TableSyncState>().Add(syncState);
                await _dbContext.SaveChangesAsync();
            }
            else
            {
                // Update customer name if it changed
                if (syncState.CustomerName != customerName)
                {
                    _logger.Information("Updating customer name for page {PageId}: '{OldName}' -> '{NewName}'",
                        pageId, syncState.CustomerName, customerName);
                    syncState.CustomerName = customerName;
                    syncState.UpdatedAt = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync();
                }
            }

            return syncState;
        }

        private async Task<TableSyncState?> GetSyncStateByPageId(string pageId)
        {
            return await _dbContext.Set<TableSyncState>()
                .FirstOrDefaultAsync(s => s.ConfluencePageId == pageId);
        }

        private async Task<bool> ShouldSyncToSharePoint(ConfluenceTableRow confluenceRow, TableSyncState syncState)
        {
            _logger.Debug("Evaluating sync need for page {PageId}:", confluenceRow.PageId);
            _logger.Debug("  - LastSyncedUtc: {LastSynced}", syncState.LastSyncedUtc);
            _logger.Debug("  - SharePointItemId: {SharePointId}", syncState.SharePointItemId);
            _logger.Debug("  - Confluence LastModified: {ConfluenceModified}", confluenceRow.LastModifiedUtc);
            _logger.Debug("  - Sync State LastConfluenceModified: {SyncStateModified}", syncState.LastConfluenceModifiedUtc);

            // Check if Confluence Sync Tracker allows sync
            if (!ShouldSyncBasedOnConfluenceSyncTracker(confluenceRow))
            {
                _logger.Debug("Skipping Confluence page {PageId} - Sync Tracker is not 'Yes'", confluenceRow.PageId);
                return false;
            }

            // Sync if:
            // 1. Never synced before
            if (syncState.LastSyncedUtc == null || string.IsNullOrEmpty(syncState.SharePointItemId))
            {
                _logger.Information("Page {PageId} needs sync: Never synced before or no SharePoint item", confluenceRow.PageId);
                return true;
            }

            // 2. Confluence page is newer than last sync
            if (confluenceRow.LastModifiedUtc > syncState.LastConfluenceModifiedUtc)
            {
                _logger.Information("Page {PageId} needs sync: Confluence modified since last sync ({ConfluenceTime} > {SyncTime})",
                    confluenceRow.PageId, confluenceRow.LastModifiedUtc, syncState.LastConfluenceModifiedUtc);
                return true;
            }

            _logger.Debug("Page {PageId} does not need SharePoint sync", confluenceRow.PageId);
            return false;
        }

        private async Task<bool> ShouldSyncToConfluence(SharePointListItem spItem, TableSyncState syncState)
        {
            // Check SyncTracker field first
            if (!ShouldSyncBasedOnSyncTracker(spItem))
            {
                _logger.Debug("Skipping SharePoint item {ItemId} - SyncTracker is not 'Yes'", spItem.Id);
                return false;
            }

            // Add debug logging like the other direction
            _logger.Debug("Evaluating sync need for SharePoint item {ItemId}:", spItem.Id);
            _logger.Debug("  - LastSyncedUtc: {LastSynced}", syncState.LastSyncedUtc);
            _logger.Debug("  - SharePoint LastModified: {SharePointModified}", spItem.LastModifiedUtc);
            _logger.Debug("  - Sync State LastSharePointModified: {SyncStateModified}", syncState.LastSharePointModifiedUtc);

            if (syncState.LastSyncedUtc == null)
            {
                _logger.Information("SharePoint item {ItemId} needs sync: Never synced before", spItem.Id);
                return true;
            }

            if (spItem.LastModifiedUtc > syncState.LastSharePointModifiedUtc)
            {
                _logger.Information("SharePoint item {ItemId} needs sync: SharePoint modified since last sync ({SharePointTime} > {SyncTime})",
                    spItem.Id, spItem.LastModifiedUtc, syncState.LastSharePointModifiedUtc);
                return true;
            }

            _logger.Debug("SharePoint item {ItemId} does not need Confluence sync", spItem.Id);
            return false;
        }

        private async Task SyncToSharePoint(ConfluenceTableRow confluenceRow, TableSyncState syncState, string siteId, string listName)
        {
            try
            {
                var fields = confluenceRow.ToSharePointFields(_configuration, "TransitionTracker");
                string itemId;

                // Step 1: Try to find existing SharePoint item by ConfluencePageId
                var existingItem = await FindExistingSharePointItem(siteId, listName, confluenceRow.PageId);

                if (existingItem != null)
                {
                    // Found existing item - UPDATE it
                    itemId = existingItem.Id;
                    _logger.Information("Found existing SharePoint item {ItemId} for page {PageId}, updating it", itemId, confluenceRow.PageId);

                    await _sharePointClient.UpdateListItemAsync(siteId, listName, itemId, fields);

                    // Update sync state with the found item ID
                    syncState.SharePointItemId = itemId;
                }
                else if (!string.IsNullOrEmpty(syncState.SharePointItemId))
                {
                    // Sync state has an item ID, but we couldn't find it - try to update anyway
                    itemId = syncState.SharePointItemId;
                    _logger.Information("Using sync state SharePoint item {ItemId} for page {PageId}", itemId, confluenceRow.PageId);

                    try
                    {
                        await _sharePointClient.UpdateListItemAsync(siteId, listName, itemId, fields);
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.Message.Contains("NotFound"))
                    {
                        // Item was deleted from SharePoint, create a new one
                        _logger.Warning("SharePoint item {ItemId} not found, creating new item for page {PageId}", itemId, confluenceRow.PageId);
                        itemId = await _sharePointClient.CreateListItemAsync(siteId, listName, fields);
                        syncState.SharePointItemId = itemId;
                    }
                }
                else
                {
                    // No existing item found and no sync state - this is a genuinely new item
                    _logger.Information("Creating new SharePoint item for page {PageId}", confluenceRow.PageId);
                    itemId = await _sharePointClient.CreateListItemAsync(siteId, listName, fields);
                    syncState.SharePointItemId = itemId;
                }

                // Update sync state
                syncState.LastConfluenceModifiedUtc = confluenceRow.LastModifiedUtc;
                syncState.LastSyncedUtc = DateTime.UtcNow;
                syncState.LastSyncSource = "Confluence";
                syncState.LastSyncStatus = "Success";
                syncState.ConfluencePageVersion = confluenceRow.PageVersion;
                syncState.UpdatedAt = DateTime.UtcNow;
                syncState.LastErrorMessage = null; // Clear any previous errors

                await _dbContext.SaveChangesAsync();

                _logger.Information("Successfully synced page {PageId} to SharePoint item {ItemId}",
                    confluenceRow.PageId, itemId);
            }
            catch (Exception ex)
            {
                syncState.LastSyncStatus = "Failed";
                syncState.LastErrorMessage = ex.Message;
                syncState.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();

                _logger.Error(ex, "Failed to sync page {PageId} to SharePoint", confluenceRow.PageId);
                throw;
            }
        }

        private async Task<SharePointListItem?> FindExistingSharePointItem(string siteId, string listName, string confluencePageId)
        {
            try
            {
                _logger.Debug("Searching for existing SharePoint item with ConfluencePageId = {PageId}", confluencePageId);

                // Get all items and search for matching ConfluencePageId
                var allItems = await _sharePointClient.GetAllListItemsAsync(siteId, listName);

                var existingItem = allItems.FirstOrDefault(item =>
                {
                    if (item.Fields.TryGetValue("ConfluencePageId", out var pageIdObj))
                    {
                        var pageIdStr = pageIdObj?.ToString();
                        return pageIdStr == confluencePageId || pageIdStr == int.Parse(confluencePageId).ToString();
                    }
                    return false;
                });

                if (existingItem != null)
                {
                    _logger.Debug("Found existing SharePoint item {ItemId} for ConfluencePageId {PageId}",
                        existingItem.Id, confluencePageId);
                }
                else
                {
                    _logger.Debug("No existing SharePoint item found for ConfluencePageId {PageId}", confluencePageId);
                }

                return existingItem;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error searching for existing SharePoint item for page {PageId}", confluencePageId);
                return null;
            }
        }

        private async Task SyncToConfluence(SharePointListItem spItem, TableSyncState syncState)
        {
            try
            {
                // Extract the SharePoint data and map it back to Confluence table format
                var confluenceTableData = MapSharePointItemToConfluenceTable(spItem);

                // Update the Confluence page table with the SharePoint data
                await _confluenceClient.UpdateTransitionTrackerFromSharePointAsync(
                    syncState.ConfluencePageId,
                    confluenceTableData);

                syncState.LastSharePointModifiedUtc = spItem.LastModifiedUtc;
                syncState.LastSyncedUtc = DateTime.UtcNow;
                syncState.LastSyncSource = "SharePoint";
                syncState.LastSyncStatus = "Success";
                syncState.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();

                _logger.Information("Successfully synced SharePoint item {ItemId} to Confluence page {PageId}",
                    spItem.Id, syncState.ConfluencePageId);
            }
            catch (Exception ex)
            {
                syncState.LastSyncStatus = "Failed";
                syncState.LastErrorMessage = ex.Message;
                syncState.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
                throw;
            }
        }


        private Dictionary<string, string> MapSharePointItemToConfluenceTable(SharePointListItem spItem)
        {
            var tableData = new Dictionary<string, string>();

            // Map SharePoint field names back to Confluence table field names
            if (spItem.Fields.TryGetValue("field_1", out var region))
                tableData["Region"] = region?.ToString() ?? "";

            if (spItem.Fields.TryGetValue("field_2", out var statusFF))
                tableData["Status FF"] = statusFF?.ToString() ?? "";

            if (spItem.Fields.TryGetValue("field_3", out var statusCust))
                tableData["Status Cust."] = statusCust?.ToString() ?? "";

            if (spItem.Fields.TryGetValue("field_4", out var phase))
                tableData["Phase"] = phase?.ToString() ?? "";

            if (spItem.Fields.TryGetValue("Go_x002d_LiveDate", out var goLiveDate))
                tableData["Go-Live Date (YYYY-MM-DD)"] = FormatDateForConfluence(goLiveDate);

            if (spItem.Fields.TryGetValue("SupportGo_x002d_LiveDate", out var supportGoLiveDate))
                tableData["Support Go-Live Date (YYYY-MM-DD)"] = FormatDateForConfluence(supportGoLiveDate);

            if (spItem.Fields.TryGetValue("field_7", out var supportImpact))
                tableData["Support Impact"] = supportImpact?.ToString() ?? "";

            if (spItem.Fields.TryGetValue("field_8", out var supportAccepted))
                tableData["Support Accepted"] = supportAccepted?.ToString() ?? "";

            if (spItem.Fields.TryGetValue("field_9", out var notes))
                tableData["Notes"] = notes?.ToString() ?? "";

            if (spItem.Fields.TryGetValue("SyncTracker", out var syncTracker))
                tableData["Sync Tracker"] = FormatBooleanForConfluence(syncTracker);

            return tableData;
        }

        private string FormatDateForConfluence(object dateValue)
        {
            if (dateValue == null) return "";
            if (DateTime.TryParse(dateValue.ToString(), out var date))
                return date.ToString("yyyy-MM-dd");
            return "";
        }

        private string FormatBooleanForConfluence(object boolValue)
        {
            if (boolValue == null) return "";

            var stringValue = boolValue.ToString()?.ToLowerInvariant();

            return stringValue switch
            {
                "true" => "Yes",
                "false" => "No",
                "yes" => "Yes",
                "no" => "No",
                "pending" => "Pending",
                _ => boolValue.ToString() // Return original if not recognized
            };
        }

        // Helper method to update SharePoint with new Confluence page info. Populates the SharePoint List field 'Customer Wiki'
        private async Task UpdateSharePointWithNewPageInfo(SharePointListItem spItem, string pageId, string siteId, string listName)
        {
            try
            {
                // Get the page URL
                var pageUrl = await _confluenceClient.GetPageUrlAsync(pageId);

                // Get the SharePoint field name for CustomerWiki from configuration
                var customerWikiFieldName = GetSharePointFieldName("CustomerWiki", "TransitionTracker");
                var confluencePageIdFieldName = GetSharePointFieldName("ConfluencePageId", "TransitionTracker");

                var updateFields = new Dictionary<string, object>
                {
                    [confluencePageIdFieldName] = int.Parse(pageId),
                    [customerWikiFieldName] = pageUrl
                };

                await _sharePointClient.UpdateListItemAsync(siteId, listName, spItem.Id, updateFields);

                _logger.Information("Updated SharePoint item {ItemId} with Confluence page {PageId} and URL {PageUrl}",
                    spItem.Id, pageId, pageUrl);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to update SharePoint item {ItemId} with new Confluence page info", spItem.Id);
                throw; // Re-throw since this is critical for the sync to work
            }
        }

        // Helper method to get SharePoint field name from configuration
        private string GetSharePointFieldName(string logicalFieldName, string listType)
        {
            var fieldMappings = _configuration.GetSection($"SharePointFieldMappings:{listType}");
            var sharePointFieldName = fieldMappings[logicalFieldName];

            if (string.IsNullOrEmpty(sharePointFieldName))
            {
                _logger.Warning("No SharePoint field mapping found for {LogicalFieldName} in {ListType}, using logical name",
                    logicalFieldName, listType);
                return logicalFieldName;
            }

            return sharePointFieldName;
        }

        #endregion
    }
}