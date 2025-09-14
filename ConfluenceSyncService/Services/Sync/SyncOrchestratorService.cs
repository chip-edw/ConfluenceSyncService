using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Services.State;
using ConfluenceSyncService.Services.Workflow;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConfluenceSyncService.Services.Sync
{
    public class SyncOrchestratorService : ISyncOrchestratorService
    {
        private readonly SharePointClient _sharePointClient;
        private readonly ConfluenceClient _confluenceClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly IConfiguration _configuration;
        private readonly ICursorStore _cursorStore;
        private readonly IWorkflowMappingProvider _mappingProvider;
        private readonly Serilog.ILogger _logger;
        private readonly ITaskIdIssuer _taskIdIssuer;
        private readonly IHostEnvironment _environment;

        // Single source-of-truth path for the workflow template JSON (no fallback).
        // You can override via appsettings: "Workflow:TemplatePath"
        private string WorkflowTemplatePath =>
            _configuration["Workflow:TemplatePath"]
            ?? Path.Combine(_environment.ContentRootPath, "Data", "Templates", "Workflow_template.json");

        public SyncOrchestratorService(
            SharePointClient sharePointClient,
            ConfluenceClient confluenceClient,
            ApplicationDbContext dbContext,
            IConfiguration configuration,
            ICursorStore cursorStore,
            IWorkflowMappingProvider mappingProvider,
            ITaskIdIssuer taskIdIssuer,
            IHostEnvironment environment)
        {
            _sharePointClient = sharePointClient;
            _confluenceClient = confluenceClient;
            _dbContext = dbContext;
            _configuration = configuration;
            _cursorStore = cursorStore;
            _mappingProvider = mappingProvider;
            _taskIdIssuer = taskIdIssuer;
            _logger = Log.ForContext<SyncOrchestratorService>();
            _environment = environment;
        }

        public async Task RunSyncAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Information("=== STARTING TABLE SYNC WORKFLOW ===");

                await Step2_UpdateConfluenceStatusText(cancellationToken);
                await Step3_SyncConfluenceToSharePoint(cancellationToken);
                await Step4_SyncSharePointToConfluence(cancellationToken);
                await Step5_ReadTransitionTrackerDeltas_ReadOnly(cancellationToken);
                await Step6_UpsertFromTrackerAndAdvanceCursor(cancellationToken);

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

                    var fullPage = await _confluenceClient.GetPageWithContentAsync(page.Id, cancellationToken);

                    if (!PageHasTransitionTable(fullPage))
                    {
                        _logger.Information("Page {PageId} missing transition table, creating it", page.Id);
                        await _confluenceClient.CreateTransitionTrackerTableAsync(page.Id, page.CustomerName, cancellationToken);
                        continue;
                    }

                    var updateSuccess = await _confluenceClient.UpdateStatusTextBasedOnColorAsync(page.Id, cancellationToken);
                    _logger.Debug("Status text update for page {PageId}: {Success}", page.Id, updateSuccess);

                    if (updateSuccess)
                    {
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
                    var fullPage = await _confluenceClient.GetPageWithContentAsync(page.Id, cancellationToken);

                    if (!PageHasTransitionTable(fullPage))
                    {
                        _logger.Warning("Page {PageId} has no transition table, skipping", page.Id);
                        continue;
                    }

                    var tableData = await _confluenceClient.ParseTransitionTrackerTableAsync(page.Id, cancellationToken);
                    var confluenceTableRow = MapToConfluenceTableRow(tableData, fullPage);

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

                    if (string.IsNullOrEmpty(pageId))
                    {
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
                                var newPageId = await _confluenceClient.CreateCustomerPageFromTemplateAsync(customerName, cancellationToken);
                                await UpdateSharePointWithNewPageInfo(spItem, newPageId, siteId, transitionTrackerList.DisplayName);
                                var newSyncState = await GetOrCreateSyncState(newPageId, customerName);

                                _logger.Information("Successfully created Confluence page {PageId} for customer {CustomerName} and updated SharePoint item {ItemId}",
                                    newPageId, customerName, spItem.Id);
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
                }
            }
        }

        // ===========================
        // STEP 5 (READ-ONLY DELTAS)
        // ===========================
        private async Task Step5_ReadTransitionTrackerDeltas_ReadOnly(CancellationToken cancellationToken)
        {
            _logger.Information("=== STEP 5: READ TRANSITION TRACKER DELTAS (READ-ONLY) ===");

            const string TrackerCursorKey = "Cursor:TransitionTracker:lastModifiedUtc";

            var cursorStr = await _cursorStore.GetAsync(TrackerCursorKey, cancellationToken);
            var since = DateTimeOffset.TryParse(cursorStr, out var parsed)
                ? parsed
                : DateTimeOffset.Parse("2000-01-01T00:00:00Z");

            var sites = StartupConfiguration.SharePointSites;
            var site = sites?.FirstOrDefault();
            if (site is null || string.IsNullOrWhiteSpace(site.SiteId))
            {
                _logger.Error("SharePoint siteId not configured");
                return;
            }
            var siteId = site.SiteId;

            var mapping = _mappingProvider.Get();
            var trackerListName = ResolveListDisplayName("transitionTracker", "Transition Tracker", "TransitionTracker");

            var items = await _sharePointClient.GetAllListItemsAsync(siteId, trackerListName);
            var recent = items
                .Where(i => i.LastModifiedUtc > since)
                .OrderBy(i => i.LastModifiedUtc)
                .ToList();

            var fTitle = GetSharePointFieldName("Customer", "TransitionTracker");
            var fCustomerId = GetSharePointFieldName("CustomerId", "TransitionTracker");
            var fRegion = GetSharePointFieldName("Region", "TransitionTracker");
            var fPhase = GetSharePointFieldName("Phase", "TransitionTracker");
            var fGoLive = GetSharePointFieldName("GoLiveDate", "TransitionTracker");
            var fSupportGoLive = GetSharePointFieldName("SupportGoLiveDate", "TransitionTracker");
            var fCustomerWiki = GetSharePointFieldName("CustomerWiki", "TransitionTracker");
            var fSyncTracker = GetSharePointFieldName("SyncTracker", "TransitionTracker");

            var deltas = new List<(SharePointListItem Item, string CustomerId, string CustomerName, string Region, string PhaseName, DateTimeOffset? GoLive, DateTimeOffset? HypercareEnd)>();

            foreach (var it in recent)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var fields = it.Fields;

                var syncVal = fields.TryGetValue(fSyncTracker, out var sv) ? sv?.ToString() : null;
                var isOn = string.Equals(syncVal, "true", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(syncVal, "yes", StringComparison.OrdinalIgnoreCase)
                           || syncVal == "1";
                if (!isOn) continue;

                var title = fields.TryGetValue(fTitle, out var t) ? t?.ToString() ?? "" : "";
                var customerId = fields.TryGetValue(fCustomerId, out var cid) ? cid?.ToString() ?? "" : "";
                var region = fields.TryGetValue(fRegion, out var r) ? r?.ToString() ?? "" : "";
                var phase = fields.TryGetValue(fPhase, out var p) ? p?.ToString() ?? "" : "";

                var goLive = fields.TryGetValue(fGoLive, out var gl) ? TryParseDate(gl) : null;
                var hypercare = fields.TryGetValue(fSupportGoLive, out var hc) ? TryParseDate(hc) : null;

                fields.TryGetValue(fCustomerWiki, out var wikiVal);
                var customerName = NameFromWikiOrTitle(wikiVal, title);

                deltas.Add((
                    Item: it,
                    CustomerId: customerId,
                    CustomerName: customerName,
                    Region: region,
                    PhaseName: phase,
                    GoLive: goLive,
                    HypercareEnd: hypercare));
            }

            _logger.Information("tracker.fetch {since} {count}", since.ToString("o"), deltas.Count);
            foreach (var d in deltas)
            {
                _logger.Information("tracker.delta {itemId} {customerId} {phaseName} {goLive} {hypercareEnd}",
                    d.Item.Id, d.CustomerId, d.PhaseName,
                    d.GoLive?.ToString("yyyy-MM-dd") ?? "null",
                    d.HypercareEnd?.ToString("yyyy-MM-dd") ?? "null");
            }
        }

        // ===========================
        // STEP 6 Upsert + Advance Cursor
        // ===========================
        private async Task Step6_UpsertFromTrackerAndAdvanceCursor(CancellationToken cancellationToken)
        {
            _logger.Information("=== STEP 6: UPSERT CUSTOMERS + PHASE TASKS, ADVANCE CURSOR ===");

            const string TrackerCursorKey = "Cursor:TransitionTracker:lastModifiedUtc";

            var cursorStr = await _cursorStore.GetAsync(TrackerCursorKey, cancellationToken);
            var since = DateTimeOffset.TryParse(cursorStr, out var parsed)
                ? parsed
                : DateTimeOffset.Parse("2000-01-01T00:00:00Z");

            bool hadBlockingSkips = false;

            var sites = StartupConfiguration.SharePointSites;
            var site = sites?.FirstOrDefault();
            if (site is null || string.IsNullOrWhiteSpace(site.SiteId))
            {
                _logger.Error("SharePoint siteId not configured");
                return;
            }
            var siteId = site.SiteId;

            var mapping = _mappingProvider.Get();
            var trackerListName = ResolveListDisplayName("transitionTracker", "Transition Tracker", "TransitionTracker");
            var customersListName = ResolveListDisplayName("customers", "TransitionCustomers", "transitionCustomers", "Transition Customers");
            var phaseTasksListName = ResolveListDisplayName("phaseTasks", "Phase Tasks & Metadata", "PhaseTasksMetadata", "phaseTasksMetadata");

            var workflowId = mapping.WorkflowId;

            var items = await _sharePointClient.GetAllListItemsAsync(siteId, trackerListName);
            var modified = items.Where(i => i.LastModifiedUtc > since).OrderBy(i => i.LastModifiedUtc).ToList();

            var fTitle = GetSharePointFieldName("Customer", "TransitionTracker");
            var fCustomerId = GetSharePointFieldName("CustomerId", "TransitionTracker");
            var fRegion = GetSharePointFieldName("Region", "TransitionTracker");
            var fPhase = GetSharePointFieldName("Phase", "TransitionTracker");
            var fGoLive = GetSharePointFieldName("GoLiveDate", "TransitionTracker");
            var fSupportGoLive = GetSharePointFieldName("SupportGoLiveDate", "TransitionTracker");
            var fCustomerWiki = GetSharePointFieldName("CustomerWiki", "TransitionTracker");
            var fSyncTracker = GetSharePointFieldName("SyncTracker", "TransitionTracker");

            var activities = LoadActivitiesSafe();

            DateTimeOffset? maxModified = since;
            foreach (var it in modified)
            {
                if (cancellationToken.IsCancellationRequested) break;
                maxModified = (maxModified is null || it.LastModifiedUtc > maxModified) ? it.LastModifiedUtc : maxModified;

                var syncVal = it.Fields.TryGetValue(fSyncTracker, out var sv) ? sv?.ToString() : null;
                var isOn = string.Equals(syncVal, "true", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(syncVal, "yes", StringComparison.OrdinalIgnoreCase)
                           || syncVal == "1";
                if (!isOn) continue;

                var title = it.Fields.TryGetValue(fTitle, out var t) ? t?.ToString() ?? "" : "";
                var customerId = it.Fields.TryGetValue(fCustomerId, out var cid) ? cid?.ToString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(customerId))
                {
                    var backfilled = await TryBackfillCustomerIdAsync(
                        siteId,
                        trackerListName,
                        customersListName,
                        it,
                        cancellationToken);

                    if (string.IsNullOrWhiteSpace(backfilled))
                    {
                        _logger.Warning("tracker.row {itemId} missing CustomerId; holding cursor (no advance)", it.Id);
                        hadBlockingSkips = true;
                        continue;
                    }

                    customerId = backfilled!;
                }
                var region = it.Fields.TryGetValue(fRegion, out var r) ? r?.ToString() ?? "" : "";
                var phaseName = it.Fields.TryGetValue(fPhase, out var p) ? p?.ToString() ?? "" : "";
                var goLive = TryParseDate(it.Fields.TryGetValue(fGoLive, out var gl) ? gl : null);
                var hypercareEnd = TryParseDate(it.Fields.TryGetValue(fSupportGoLive, out var hc) ? hc : null);

                it.Fields.TryGetValue(fCustomerWiki, out var wikiVal);
                var customerName = NameFromWikiOrTitle(wikiVal, title);

                var phaseId = await GetOrCreatePhaseIdAsync(siteId, phaseTasksListName, customerId, phaseName, goLive, cancellationToken);
                _logger.Information("phase.resolve {customerId} {phaseId}", customerId, phaseId);

                await UpsertTransitionCustomerAsync(siteId, customersListName, customerId, customerName, region, phaseId,
                    hypercareEnd, cancellationToken);

                await UpsertPhaseTasksAsync(siteId, phaseTasksListName, customerId, customerName, phaseId, phaseName, goLive,
                    hypercareEnd, activities, workflowId, region, cancellationToken);
            }

            if (!hadBlockingSkips && maxModified.HasValue && maxModified.Value > since)
            {
                var newCursor = maxModified.Value.ToUniversalTime().ToString("o");
                await _cursorStore.SetAsync(TrackerCursorKey, newCursor, cancellationToken);
                _logger.Information("cursor.advance {old} -> {new}", since.ToString("o"), newCursor);
            }
            else if (hadBlockingSkips)
            {
                _logger.Information("cursor.advance held: missing CustomerId on one or more rows");
            }
            else
            {
                _logger.Information("cursor.advance no-op (no newer items)");
            }
        }

        #region Helper Methods

        private async Task UpsertTransitionCustomerAsync(string siteId, string customersListName, string customerId, string customerName,
            string? region, string phaseId, DateTimeOffset? supportGoLive, CancellationToken ct)
        {
            var items = await _sharePointClient.GetAllListItemsAsync(siteId, customersListName);

            var fCustomerId = MapField("TransitionCustomers", "CustomerId");
            var fCustomerName = MapField("TransitionCustomers", "Customer");
            var fActivePhase = MapField("TransitionCustomers", "ActivePhaseID");
            var fRegion = MapField("TransitionCustomers", "Region");

            SharePointListItem? existing = items.FirstOrDefault(i =>
                i.Fields.TryGetValue(fCustomerId, out var cid) && (cid?.ToString() ?? "") == customerId);

            var fields = new Dictionary<string, object>
            {
                [fCustomerId] = customerId,
                [fCustomerName] = customerName
            };
            if (!string.IsNullOrWhiteSpace(region)) fields[fRegion] = region;

            if (supportGoLive.HasValue)
            {
                fields[fActivePhase] = phaseId;
            }

            if (existing is null)
            {
                await _sharePointClient.CreateListItemAsync(siteId, customersListName, fields);
                _logger.Information("customer.upsert {customerId} created activePhase={phaseId}", customerId, phaseId);
            }
            else
            {
                await _sharePointClient.UpdateListItemAsync(siteId, customersListName, existing.Id, fields);
                _logger.Information("customer.upsert {customerId} updated activePhase={phaseId}", customerId, phaseId);
            }
        }

        // === Template loading: single file, no fallback ===
        private IReadOnlyList<ActivitySpec> LoadActivitiesSafe()
        {
            // Single canonical path; no fallbacks
            var jsonPath = Path.Combine(_environment.ContentRootPath, "Data", "Templates", "Workflow_template.json");

            _logger.Information("Loading workflow template (new schema) from {Path}", jsonPath);

            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"Workflow template not found at {jsonPath}");

            var jsonContent = File.ReadAllText(jsonPath);
            return ParseWorkflowTemplate(jsonContent);
        }

        // Expects the NEW flat array shape; allows "_comment" fields (ignored).
        private IReadOnlyList<ActivitySpec> ParseWorkflowTemplate(string jsonContent)
        {
            using var document = JsonDocument.Parse(jsonContent);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Workflow template root must be a JSON object with 'Activities'.");

            var root = document.RootElement;

            // Optional: read WorkflowId if you ever want to compare it to mapping.WorkflowId
            if (root.TryGetProperty("WorkflowId", out var wfIdProp))
            {
                var wfId = wfIdProp.GetString();
                _logger.Information("Workflow template declares WorkflowId={WorkflowId}", wfId);
            }

            if (!root.TryGetProperty("Activities", out var activitiesProp) || activitiesProp.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Workflow template must contain an 'Activities' array.");

            var list = new List<ActivitySpec>();

            foreach (var taskElement in activitiesProp.EnumerateArray())
            {
                // Required fields in the new schema
                var taskName = taskElement.GetProperty("TaskName").GetString() ?? "";
                var category = taskElement.GetProperty("Category").GetString() ?? "";
                var role = taskElement.GetProperty("Role").GetString() ?? "";
                var anchor = taskElement.GetProperty("AnchorDateType").GetString() ?? "";
                var startOffset = taskElement.GetProperty("StartOffsetDays").GetInt32();
                var duration = taskElement.GetProperty("DurationBusinessDays").GetInt32();

                // Prefer explicit Key; fall back to a deterministic key if missing
                string key = taskElement.TryGetProperty("Key", out var keyProp)
                    ? (keyProp.GetString() ?? "")
                    : "";

                if (string.IsNullOrWhiteSpace(key))
                    key = GenerateTaskKey(taskName);

                list.Add(new ActivitySpec
                {
                    Key = key,
                    TaskCategory = category,
                    TaskName = taskName,
                    DefaultRole = role,
                    AnchorDateType = anchor,
                    StartOffsetDays = startOffset,
                    DurationBusinessDays = duration
                });

                _logger.Debug("Parsed activity: {Key} [{Category}] {TaskName} (Offset={Start}, Duration={Dur})",
                    key, category, taskName, startOffset, duration);
            }

            _logger.Information("Workflow template parsed successfully: {Count} activities", list.Count);
            return list;
        }

        private static string GenerateTaskKey(string taskName)
        {
            var cleaned = taskName
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim()
                .ToLowerInvariant();

            var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !new[] { "the", "and", "for", "with", "from" }.Contains(w))
                .Take(3)
                .ToArray();

            if (words.Length == 0)
            {
                return cleaned.Length > 10 ? cleaned.Substring(0, 10) : cleaned;
            }

            return string.Join("-", words);
        }

        private async Task UpsertPhaseTasksAsync(
            string siteId,
            string phaseTasksListName,
            string customerId,
            string customerName,
            string phaseId,
            string phaseName,
            DateTimeOffset? goLive,
            DateTimeOffset? hypercareEnd,
            IReadOnlyList<ActivitySpec> activities,
            string workflowId,
            string region,
            CancellationToken ct)
        {
            var listKey = "PhaseTasks";

            var fCorrelation = MapField("Phase Tasks & Metadata", "CorrelationId");
            var fCustomerId = MapField("Phase Tasks & Metadata", "CustomerId");
            var fCustomer = MapField("Phase Tasks & Metadata", "Customer");
            var fPhaseId = MapField("Phase Tasks & Metadata", "PhaseID");
            var fPhaseName = MapField("Phase Tasks & Metadata", "PhaseName");
            var fTaskName = MapField("Phase Tasks & Metadata", "TaskName");
            var fTaskId = MapField("Phase Tasks & Metadata", "TaskId");
            var fTaskCategory = MapField("Phase Tasks & Metadata", "TaskCategory");
            var fRole = MapField("Phase Tasks & Metadata", "Role");
            var fAnchorType = MapField("Phase Tasks & Metadata", "AnchorDateType");
            var fStartOffset = MapField("Phase Tasks & Metadata", "StartOffsetDays");
            var fDuration = MapField("Phase Tasks & Metadata", "DurationBusinessDays");
            var fGoLive = MapField("Phase Tasks & Metadata", "GoLiveDate");
            var fHypercare = MapField("Phase Tasks & Metadata", "HypercareEndDate");
            var fStatus = MapField("Phase Tasks & Metadata", "Status");


            // === SELF-HEALING: Detect and fix TaskIdMap records missing notification fields ===
            try
            {
                var recordsNeedingHealing = await _dbContext.TaskIdMaps
                    .Where(x => x.CustomerId == customerId
                             && x.State == "linked"
                             && (x.NextChaseAtUtcCached == null || x.TeamId == null || x.ChannelId == null)
                             && (x.Status == null || x.Status != "Completed"))
                    .ToListAsync(ct);

                if (recordsNeedingHealing.Any())
                {
                    // Check customer's SyncTracker status
                    var syncState = await _dbContext.TableSyncStates
                        .FirstOrDefaultAsync(s => s.CustomerName == customerName, ct);

                    if (syncState?.SyncTracker == "Yes")
                    {
                        var teamId = StartupConfiguration.TeamsConfiguration?.TeamId;
                        var channelId = StartupConfiguration.TeamsConfiguration?.ChannelId;

                        foreach (var record in recordsNeedingHealing)

                            await _dbContext.SaveChangesAsync(ct);
                        _logger.Information("Self-healing completed for customer {CustomerId}: {Count} records updated",
                            customerId, recordsNeedingHealing.Count);
                    }
                    else
                    {
                        _logger.Information("Self-healing skipped for customer {CustomerId}: SyncTracker={SyncTracker}",
                            customerId, syncState?.SyncTracker ?? "null");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Self-healing failed for customer {CustomerId}", customerId);
            }
            // === END SELF-HEALING ===


            foreach (var a in activities)
            {
                var correlation = Sha1($"{customerId}|{phaseId}|{workflowId}|{a.Key}");

                var fields = new Dictionary<string, object>
                {
                    [fCorrelation] = correlation,
                    [fCustomerId] = customerId,
                    [fCustomer] = customerName,
                    [fPhaseId] = phaseId,
                    [fPhaseName] = phaseName,
                    [fTaskName] = a.TaskName,
                    [fTaskCategory] = a.TaskCategory,
                    [fRole] = a.DefaultRole,
                    [fAnchorType] = a.AnchorDateType,
                    [fStartOffset] = a.StartOffsetDays,
                    [fDuration] = a.DurationBusinessDays
                };

                if (goLive.HasValue)
                    fields[fGoLive] = goLive.Value.ToString("yyyy-MM-ddT00:00:00Z");
                if (hypercareEnd.HasValue)
                    fields[fHypercare] = hypercareEnd.Value.ToString("yyyy-MM-ddT00:00:00Z");

                var map = await _dbContext.TaskIdMaps.AsNoTracking().FirstOrDefaultAsync(
                    x => x.ListKey == listKey && x.CorrelationId == correlation && x.State == "linked", ct);

                ConfluenceSyncService.Models.TaskIdMap? detMap = null;
                if (map is null)
                {
                    detMap = await _dbContext.TaskIdMaps.AsNoTracking()
                        .Where(x => x.ListKey == listKey
                                 && x.CustomerId == customerId
                                 && x.PhaseName == phaseName
                                 && x.TaskName == a.TaskName
                                 && x.WorkflowId == workflowId)
                        .OrderByDescending(x => x.CreatedUtc)
                        .FirstOrDefaultAsync(ct);
                }

                string? spItemId = map?.SpItemId ?? detMap?.SpItemId;
                int? mappedId = map?.TaskId ?? detMap?.TaskId;
                bool haveLinked = !string.IsNullOrEmpty(spItemId);
                bool haveReserved = detMap is not null && detMap.State == "reserved" && string.IsNullOrEmpty(detMap.SpItemId);

                if (map is null && detMap is not null && detMap.CorrelationId != correlation && haveLinked)
                {
                    var tracked = await _dbContext.TaskIdMaps.FirstOrDefaultAsync(x => x.TaskId == detMap.TaskId, ct);
                    if (tracked is not null)
                    {
                        tracked.CorrelationId = correlation;
                        await _dbContext.SaveChangesAsync(ct);
                        _logger.Information("task.map refresh correlation TaskId {TaskId}: {Old} -> {New}",
                            detMap.TaskId, detMap.CorrelationId, correlation);
                    }
                }

                if (haveLinked)
                {
                    try
                    {
                        _logger.Information("Processing linked item: TaskId={TaskId}, SpItemId={SpItemId}, Correlation={Correlation}",
                            mappedId, spItemId, correlation);

                        if (fStatus == null)
                        {
                            _logger.Error("fStatus field mapping returned null for 'Phase Tasks & Metadata'.'Status'");
                            continue;
                        }

                        var current = await _sharePointClient.GetListItemAsync(siteId, phaseTasksListName, spItemId!);

                        if (current == null)
                        {
                            _logger.Warning("SharePoint item not found in list '{ListName}': SpItemId={SpItemId}, treating as unlinked", spItemId);
                            continue;
                        }

                        if (current.Fields == null)
                        {
                            _logger.Warning("SharePoint item {SpItemId} has null Fields collection", spItemId);
                            continue;
                        }

                        _logger.Information("SharePoint item {SpItemId} loaded with {FieldCount} fields", spItemId, current.Fields.Count);

                        string? curStatus = null;
                        if (current.Fields.TryGetValue(fStatus, out var s))
                        {
                            if (s is not null)
                            {
                                curStatus = s.ToString();
                                _logger.Information("Status field found: '{Status}' for SpItemId={SpItemId}", curStatus, spItemId);
                            }
                            else
                            {
                                _logger.Information("Status field exists but is null for SpItemId={SpItemId}", spItemId);
                            }
                        }
                        else
                        {
                            _logger.Information("Status field '{FieldName}' not found in SpItemId={SpItemId}", fStatus, spItemId);
                        }

                        if (string.Equals(curStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Information("task.upsert {Correlation} skip reschedule: already Completed (TaskId={TaskId}, ItemId={ItemId})",
                                correlation, mappedId, spItemId);
                            continue;
                        }

                        if (fCorrelation == null || fAnchorType == null || fStartOffset == null ||
                            fDuration == null || fTaskCategory == null || fRole == null)
                        {
                            _logger.Error("One or more required field mappings are null: fCorrelation={C}, fAnchorType={A}, fStartOffset={S}, fDuration={D}, fTaskCategory={T}, fRole={R}",
                                fCorrelation, fAnchorType, fStartOffset, fDuration, fTaskCategory, fRole);
                            continue;
                        }

                        var patch = new Dictionary<string, object?>();
                        patch[fCorrelation] = correlation;
                        patch[fAnchorType] = a.AnchorDateType;
                        patch[fStartOffset] = a.StartOffsetDays;
                        patch[fDuration] = a.DurationBusinessDays;
                        patch[fTaskCategory] = a.TaskCategory;
                        patch[fRole] = a.DefaultRole;

                        if (goLive.HasValue)
                        {
                            if (fGoLive != null && fields.ContainsKey(fGoLive))
                                patch[fGoLive] = fields[fGoLive];
                            else
                                _logger.Warning("fGoLive field mapping is null or not in fields dictionary");
                        }

                        if (hypercareEnd.HasValue)
                        {
                            if (fHypercare != null && fields.ContainsKey(fHypercare))
                                patch[fHypercare] = fields[fHypercare];
                            else
                                _logger.Warning("fHypercare field mapping is null or not in fields dictionary");
                        }

                        _logger.Information("Updating SharePoint item {SpItemId} with {PatchCount} fields", spItemId, patch.Count);

                        await _sharePointClient.UpdateListItemAsync(siteId, phaseTasksListName, spItemId!, patch);

                        _logger.Information("task.upsert {Correlation} updated anchor fields (TaskId={TaskId}, ItemId={ItemId})",
                            correlation, mappedId, spItemId);

                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error processing linked SharePoint item: TaskId={TaskId}, SpItemId={SpItemId}, Correlation={Correlation}",
                            mappedId, spItemId, correlation);
                        continue;
                    }
                }

                if (haveReserved && mappedId.HasValue)
                {
                    fields[fStatus] = "Not Started";
                    fields[fTaskId] = mappedId.Value;

                    _logger.Information("=== CREATE (reserved reuse) === Site:{SiteId} List:{List} Activity:{Key}-{Name} Correlation:{Correlation}",
                        siteId, phaseTasksListName, a.Key, a.TaskName, correlation);

                    var newId = await _sharePointClient.CreateListItemAsync(siteId, phaseTasksListName, fields);
                    await _taskIdIssuer.LinkToSharePointAsync(mappedId.Value, newId, ct);

                    _logger.Information("task.upsert {Correlation} created (reserved reuse) TaskId={TaskId}, ItemId={ItemId}",
                        correlation, mappedId.Value, newId);

                    continue;
                }

                {
                    fields[fStatus] = "Not Started";

                    var taskId = await _taskIdIssuer.ReserveAsync(
                        listKey: listKey,
                        correlationId: correlation,
                        customerId: customerId,
                        phaseName: phaseName,
                        taskName: a.TaskName,
                        workflowId: workflowId,
                        ct: ct);

                    fields[fTaskId] = taskId;

                    _logger.Information("=== CREATE === Site:{SiteId} List:{List} Activity:{Key}-{Name} Correlation:{Correlation}",
                        siteId, phaseTasksListName, a.Key, a.TaskName, correlation);

                    var id = await _sharePointClient.CreateListItemAsync(siteId, phaseTasksListName, fields);

                    await _taskIdIssuer.LinkToSharePointAsync(taskId, id, ct);

                    _logger.Information("task.upsert {Correlation} created {Task} with ID {ItemId}", correlation, a.TaskName, id);
                }
            }
        }

        private bool ShouldSyncBasedOnSyncTracker(SharePointListItem spItem)
        {
            if (spItem.Fields.TryGetValue("SyncTracker", out var syncTrackerValue))
            {
                var syncTracker = syncTrackerValue?.ToString()?.ToLowerInvariant();

                return syncTracker switch
                {
                    "true" => true,
                    "yes" => true,
                    "1" => true,
                    _ => false
                };
            }
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

            if (!ShouldSyncBasedOnConfluenceSyncTracker(confluenceRow))
            {
                _logger.Debug("Skipping Confluence page {PageId} - Sync Tracker is not 'Yes'", confluenceRow.PageId);
                return false;
            }

            if (syncState.LastSyncedUtc == null || string.IsNullOrEmpty(syncState.SharePointItemId))
            {
                _logger.Information("Page {PageId} needs sync: Never synced before or no SharePoint item", confluenceRow.PageId);
                return true;
            }

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
            if (!ShouldSyncBasedOnSyncTracker(spItem))
            {
                _logger.Debug("Skipping SharePoint item {ItemId} - SyncTracker is not 'Yes'", spItem.Id);
                return false;
            }

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

                var existingItem = await FindExistingSharePointItem(siteId, listName, confluenceRow.PageId);

                if (existingItem != null)
                {
                    itemId = existingItem.Id;
                    _logger.Information("Found existing SharePoint item {ItemId} for page {PageId}, updating it", itemId, confluenceRow.PageId);

                    await _sharePointClient.UpdateListItemAsync(siteId, listName, itemId, fields);

                    syncState.SharePointItemId = itemId;
                }
                else if (!string.IsNullOrEmpty(syncState.SharePointItemId))
                {
                    itemId = syncState.SharePointItemId;
                    _logger.Information("Using sync state SharePoint item {ItemId} for page {PageId}", itemId, confluenceRow.PageId);

                    try
                    {
                        await _sharePointClient.UpdateListItemAsync(siteId, listName, itemId, fields);
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.Message.Contains("NotFound"))
                    {
                        _logger.Warning("SharePoint item {ItemId} not found, creating new item for page {PageId}", itemId, confluenceRow.PageId);
                        itemId = await _sharePointClient.CreateListItemAsync(siteId, listName, fields);
                        syncState.SharePointItemId = itemId;
                    }
                }
                else
                {
                    _logger.Information("Creating new SharePoint item for page {PageId}", confluenceRow.PageId);
                    itemId = await _sharePointClient.CreateListItemAsync(siteId, listName, fields);
                    syncState.SharePointItemId = itemId;
                }

                syncState.LastConfluenceModifiedUtc = confluenceRow.LastModifiedUtc;
                syncState.LastSyncedUtc = DateTime.UtcNow;
                syncState.LastSyncSource = "Confluence";
                syncState.LastSyncStatus = "Success";
                syncState.ConfluencePageVersion = confluenceRow.PageVersion;
                syncState.UpdatedAt = DateTime.UtcNow;
                syncState.LastErrorMessage = null;

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
                var confluenceTableData = MapSharePointItemToConfluenceTable(spItem);

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
                _ => boolValue.ToString()
            };
        }

        private async Task UpdateSharePointWithNewPageInfo(SharePointListItem spItem, string pageId, string siteId, string listName)
        {
            try
            {
                var pageUrl = await _confluenceClient.GetPageUrlAsync(pageId);

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
                throw;
            }
        }

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

        private string ResolveListDisplayName(string primaryKey, string fallbackDisplayName, params string[] alternateKeys)
        {
            var lists = _mappingProvider.Get().Lists;

            if (lists.TryGetValue(primaryKey, out var lm)) return lm.Name;
            foreach (var k in alternateKeys)
                if (lists.TryGetValue(k, out lm)) return lm.Name;

            var exact = lists.Values.FirstOrDefault(v =>
                string.Equals(v.Name, fallbackDisplayName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.Name;

            if (primaryKey.Contains("customer", StringComparison.OrdinalIgnoreCase))
            {
                var guess = lists.Values.FirstOrDefault(v => v.Name.Contains("Customer", StringComparison.OrdinalIgnoreCase));
                if (guess != null) return guess.Name;
            }
            if (primaryKey.Contains("phase", StringComparison.OrdinalIgnoreCase))
            {
                var guess = lists.Values.FirstOrDefault(v => v.Name.Contains("Phase", StringComparison.OrdinalIgnoreCase));
                if (guess != null) return guess.Name;
            }
            if (primaryKey.Contains("tracker", StringComparison.OrdinalIgnoreCase))
            {
                var guess = lists.Values.FirstOrDefault(v => v.Name.Contains("Transition", StringComparison.OrdinalIgnoreCase) &&
                                                             v.Name.Contains("Tracker", StringComparison.OrdinalIgnoreCase));
                if (guess != null) return guess.Name;
            }

            throw new KeyNotFoundException(
                $"List mapping not found for key '{primaryKey}'. Tried alternates: [{string.Join(", ", alternateKeys)}]. " +
                $"Available keys: {string.Join(", ", lists.Keys)}");
        }

        /// <summary>
        /// Calculates when a task becomes due based on anchor date + offset days.
        /// Returns null if anchor date is missing or calculation cannot be performed.
        /// </summary>
        private static DateTimeOffset? CalculateTaskDueDate(
            DateTimeOffset? goLiveDate,
            DateTimeOffset? hypercareEndDate,
            string? anchorDateType,
            int startOffsetDays)
        {
            if (string.IsNullOrWhiteSpace(anchorDateType))
            {
                return null;
            }

            DateTimeOffset? anchorDate = anchorDateType.ToLowerInvariant() switch
            {
                "golive" => goLiveDate,
                "hypercareend" => hypercareEndDate,
                _ => null
            };

            if (!anchorDate.HasValue)
            {
                return null;
            }

            // Add offset days (can be negative for tasks before anchor date)
            return anchorDate.Value.AddDays(startOffsetDays);
        }

        // ===== GUID Related helpers =====
        private static Guid GuidV5(Guid ns, string name)
        {
            using var sha1 = SHA1.Create();
            var nsBytes = ns.ToByteArray();
            SwapByteOrder(nsBytes);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            sha1.TransformBlock(nsBytes, 0, nsBytes.Length, null, 0);
            sha1.TransformFinalBlock(nameBytes, 0, nameBytes.Length);
            var hash = sha1.Hash!;

            var newGuid = new byte[16];
            Array.Copy(hash, 0, newGuid, 0, 16);
            newGuid[6] = (byte)((newGuid[6] & 0x0F) | (5 << 4));
            newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);
            SwapByteOrder(newGuid);
            return new Guid(newGuid);
        }
        private static void SwapByteOrder(byte[] guid)
        {
            void Swap(int a, int b) { var t = guid[a]; guid[a] = guid[b]; guid[b] = t; }
            Swap(0, 3); Swap(1, 2); Swap(4, 5); Swap(6, 7);
        }

        private static readonly Guid Namespace_ConfluenceSyncService =
            new Guid("723049de-0ae9-49db-9a87-4d68e096abbd");

        private async Task<string?> TryBackfillCustomerIdAsync(
            string siteId,
            string trackerListName,
            string customersListName,
            SharePointListItem trackerItem,
            CancellationToken ct)
        {
            var fTitle = GetSharePointFieldName("Title", "TransitionTracker");
            var fCustomerId = GetSharePointFieldName("CustomerId", "TransitionTracker");
            var fCustomerWiki = GetSharePointFieldName("CustomerWiki", "TransitionTracker");
            var fConfluenceId = GetSharePointFieldName("ConfluencePageId", "TransitionTracker");
            var title = trackerItem.Fields.TryGetValue(fTitle, out var t) ? t?.ToString() ?? "" : "";
            var wiki = trackerItem.Fields.TryGetValue(fCustomerWiki, out var w) ? w?.ToString() ?? "" : "";
            var pageId = trackerItem.Fields.TryGetValue(fConfluenceId, out var pid) ? pid?.ToString() ?? "" : "";

            var fCust_CustomerId = MapField("TransitionCustomers", "CustomerId");
            var fCust_Name = MapField("TransitionCustomers", "Customer");
            var customers = await _sharePointClient.GetAllListItemsAsync(siteId, customersListName);
            var match = customers.FirstOrDefault(i =>
            {
                var nameMatches = i.Fields.TryGetValue(fCust_Name, out var nm)
                                  && string.Equals(nm?.ToString() ?? "", title, StringComparison.OrdinalIgnoreCase);
                if (nameMatches) return true;
                return false;
            });
            if (match != null && match.Fields.TryGetValue(fCust_CustomerId, out var existingIdObj))
            {
                var existingId = existingIdObj?.ToString();
                if (!string.IsNullOrWhiteSpace(existingId))
                {
                    await _sharePointClient.UpdateListItemAsync(siteId, trackerListName, trackerItem.Id,
                        new Dictionary<string, object> { [fCustomerId] = existingId });
                    _logger.Information("tracker.backfill customerId (reuse) item={itemId} id={id}", trackerItem.Id, existingId);
                    return existingId;
                }
            }

            var key = !string.IsNullOrWhiteSpace(wiki) ? $"wiki:{wiki}"
                    : !string.IsNullOrWhiteSpace(pageId) ? $"page:{pageId}"
                    : $"name:{title}";
            var newGuid = GuidV5(Namespace_ConfluenceSyncService, key).ToString();

            await _sharePointClient.UpdateListItemAsync(siteId, trackerListName, trackerItem.Id,
                new Dictionary<string, object> { [fCustomerId] = newGuid });
            _logger.Information("tracker.backfill customerId (generated) item={itemId} id={id} from={key}", trackerItem.Id, newGuid, key);
            return newGuid;
        }

        private static DateTimeOffset? TryParseDate(object? val)
        {
            if (val is null) return null;
            if (val is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            if (val is string s && DateTimeOffset.TryParse(s, out var dto)) return dto.ToUniversalTime();
            return null;
        }

        private static string NameFromWikiOrTitle(object? wikiFieldValue, string title)
        {
            if (wikiFieldValue is null) return title;
            var s = wikiFieldValue.ToString();
            if (string.IsNullOrWhiteSpace(s)) return title;
            return Uri.IsWellFormedUriString(s, UriKind.Absolute) ? title : s;
        }

        private async Task<string> GetOrCreatePhaseIdAsync(
            string siteId, string phaseTasksListName, string customerId, string phaseName, DateTimeOffset? goLive, CancellationToken ct)
        {
            var all = await _sharePointClient.GetAllListItemsAsync(siteId, phaseTasksListName);

            var fCustomerId = MapField("Phase Tasks & Metadata", "CustomerId");
            var fPhaseName = MapField("Phase Tasks & Metadata", "PhaseName");
            var fPhaseId = MapField("Phase Tasks & Metadata", "PhaseID");
            var fGoLive = MapField("Phase Tasks & Metadata", "GoLiveDate");

            foreach (var it in all)
            {
                if (!it.Fields.TryGetValue(fCustomerId, out var cid) || (cid?.ToString() ?? "") != customerId) continue;
                if (!it.Fields.TryGetValue(fPhaseName, out var pn) || !string.Equals(pn?.ToString(), phaseName, StringComparison.OrdinalIgnoreCase)) continue;

                var existingGoLive = TryParseDate(it.Fields.TryGetValue(fGoLive, out var gl) ? gl : null);
                if (SameDate(existingGoLive, goLive))
                {
                    if (it.Fields.TryGetValue(fPhaseId, out var pid) && !string.IsNullOrWhiteSpace(pid?.ToString()))
                        return pid!.ToString()!;
                }
            }

            return Guid.NewGuid().ToString();
        }

        private string MapField(string section, string logical)
        {
            var s = _configuration.GetSection($"SharePointFieldMappings:{section}");
            return string.IsNullOrWhiteSpace(s[logical]) ? logical : s[logical]!;
        }

        private static string Sha1(string input)
        {
            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static bool SameDate(DateTimeOffset? a, DateTimeOffset? b)
        {
            if (a is null || b is null) return false;
            return a.Value.UtcDateTime.Date == b.Value.UtcDateTime.Date;
        }

        #endregion
    }
}
