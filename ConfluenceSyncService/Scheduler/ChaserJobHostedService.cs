using ConfluenceSyncService.Data;
using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Options;
using ConfluenceSyncService.Security; // IHmacSigner, AckSignerOptions
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Teams;
using ConfluenceSyncService.Time;
using ConfluenceSyncService.Utilities;
using Microsoft.Extensions.Options;
using System.Data;

namespace ConfluenceSyncService.Scheduler;

public sealed class ChaserJobHostedService : BackgroundService
{
    private readonly Serilog.ILogger _log;
    private readonly ChaserJobOptions _opts;
    private readonly string _dbPath;
    private readonly SharePointClient _sp;
    private readonly INotificationService _teams;
    private readonly IHmacSigner _signer;
    private readonly AckSignerOptions _signerOpts;
    private readonly AckLinkOptions _ackPolicy;
    private readonly ICategoryOrderProvider _categoryOrderProvider;

    private int _consecutiveFailures;

    public ChaserJobHostedService(
        Serilog.ILogger log,
        IOptions<ChaserJobOptions> opts,
        SharePointClient sp,
        INotificationService teams,
        IHmacSigner signer,
        IOptions<AckSignerOptions> signerOpts,
        IOptions<AckLinkOptions> ackPolicyOpts,
        ICategoryOrderProvider categoryOrderProvider,
        IConfiguration config,
        IHostEnvironment env)
    {
        _log = log.ForContext("Service", nameof(ChaserJobHostedService));
        _opts = opts.Value;

        // Derive SQLite file path from the configured connection string (same fallback as your EF setup)
        var cs = config.GetConnectionString("ConfluenceSync");
        _dbPath = ExtractSqlitePathOrFallback(cs, env.ContentRootPath);

        _sp = sp;
        _teams = teams;
        _signer = signer;
        _signerOpts = signerOpts.Value;
        _ackPolicy = ackPolicyOpts.Value;
        _categoryOrderProvider = categoryOrderProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.Information("ChaserJob: Disabled by config. Skipping execution.");
            return;
        }

        // Ensure TaskIdMap has chaser cache columns
        SqliteSchemaUpgrader.EnsureChaserColumns(_dbPath, _log);

        _log.Information("ChaserJobConfig {@cfg}", new
        {
            _opts.Enabled,
            _opts.CadenceMinutes,
            _opts.BatchSize,
            _opts.QuerySource
        });

        var cadence = TimeSpan.FromMinutes(Math.Max(1, _opts.CadenceMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                await RunOnceAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _log.Error(ex, "ChaserJob: Unhandled error (count={Count})", _consecutiveFailures);
                if (_consecutiveFailures >= _opts.Safety.MaxConsecutiveFailures)
                {
                    var coolOff = TimeSpan.FromMinutes(_opts.Safety.CoolOffMinutes);
                    _log.Warning("ChaserJob: Cooling off for {CoolOff}", coolOff);
                    await Task.Delay(coolOff, stoppingToken);
                    _consecutiveFailures = 0;
                }
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var delay = cadence - elapsed;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // Set in appsettings.json in the chaserJobOptions. True is used to prevent any Teams/SP updates while testing
        bool dryRunMode = _opts.DryRun;

        // 1) fetch candidates from SQLite cache
        var due = await SqliteQueries.GetDueChaserCandidatesAsync(_dbPath, _opts.BatchSize, _log, ct);
        _log.Information("SqliteCandidateFetch count={Count}", due.Count);

        // DEBUGGING LOGGING:
        _log.Debug("=== DEBUGGING: All Due Candidates ===");
        foreach (var task in due)
        {
            _log.Debug("DueTask: TaskId={TaskId}, TaskName={TaskName}, SpItemId={SpItemId}, CustomerId={CustomerId}, StartOffsetDays={StartOffsetDays}",
                task.TaskId, task.TaskName, task.SpItemId, task.CustomerId, task.StartOffsetDays);
        }
        _log.Debug("=== END DEBUG LIST ===");

        if (due.Count == 0) return;

        // SEQUENTIAL WORKFLOW DEPENDENCY FILTERING:
        var filteredTasks = await ApplyWorkflowDependencyFilterAsync(due, ct);
        _log.Information("WorkflowFiltering: Original={Original}, Filtered={Filtered}", due.Count, filteredTasks.Count);

        foreach (var task in filteredTasks)
        {
            _log.Debug("FilteredTask: TaskId={TaskId}, TaskName={TaskName}, CustomerId={CustomerId}, StartOffsetDays={StartOffsetDays}",
                task.TaskId, task.TaskName, task.CustomerId, task.StartOffsetDays);
        }

        foreach (var t in filteredTasks) // Changed from 'due' to 'filteredTasks'
        {
            _log.Information("Processing task: TaskId={TaskId}, TeamId={TeamId}, ChannelId={ChannelId}, RootMessageId={RootMessageId}," +
                " SpItemId={SpItemId}", t.TaskId, t.TeamId, t.ChannelId, t.RootMessageId, t.SpItemId);

            ct.ThrowIfCancellationRequested();

            // 2) SP confirm by item id: Status + DueDateUtc + CompanyName
            var statusDue = await _sp.GetTaskStatusAndDueUtcAsync(t.SpItemId, ct);
            if (statusDue is null)
            {
                _log.Warning("SpConfirmStatus: missing itemId={SpItemId}", t.SpItemId);
                continue;
            }

            // Enrich the candidate with SharePoint data for Teams notification
            var enrichedCandidate = t with
            {
                CompanyName = statusDue.CompanyName,
                DueDateUtc = statusDue.DueDateUtc
                // PhaseName already populated from database
            };

            _log.Debug("EnrichedCandidate: TaskId={TaskId}, Company='{CompanyName}', Due={DueDateUtc}, Phase='{PhaseName}'",
                enrichedCandidate.TaskId, enrichedCandidate.CompanyName, enrichedCandidate.DueDateUtc, enrichedCandidate.PhaseName);


            if (string.Equals(statusDue.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                // Cache completion status to prevent future queries
                await SqliteQueries.UpdateTaskStatusAsync(_dbPath, t.TaskId, "Completed", _log, ct);
                _log.Information("SkipCompleted taskId={TaskId} (cached status)", t.TaskId);
                continue;
            }
            if (statusDue.DueDateUtc is DateTimeOffset dd && dd > DateTimeOffset.UtcNow)
            {
                _log.Information("SkipNotDue taskId={TaskId}", t.TaskId);
                continue;
            }

            // 3) business-day send window
            var inWindow = BusinessDayHelper.IsWithinWindow(t.Region, _opts.BusinessWindow.StartHourLocal, _opts.BusinessWindow.EndHourLocal, _opts.BusinessWindow.CushionHours, DateTimeOffset.UtcNow);
            var nextSendUtc = BusinessDayHelper.NextBusinessDayAtHourUtc(t.Region, _opts.SendHourLocal, DateTimeOffset.UtcNow);

            _log.Debug("ChaserWindowCheck taskId={TaskId} inWindow={InWindow} nextSendUtc={Next}", t.TaskId, inWindow, nextSendUtc);

            // If we're not in the send window, we only want to schedule nextChaseAtUtc.
            // In DryRun, DO NOT write to SharePoint; just log what would happen.
            // (Note: even when in the window, we still need the nextSendUtc for TTL/ACK.)
            if (!inWindow)
            {
                if (dryRunMode)
                {
                    _log.Information("DRY RUN: Would update SP (NextChaseAtUtc) for SpItemId={SpItemId} to {Next}", t.SpItemId, nextSendUtc);
                    // Optional: keep local cache fresh without touching SP
                    await SqliteQueries.UpdateNextChaseCachedAsync(_dbPath, t.TaskId, nextSendUtc, _log, ct);
                }
                else
                {
                    await _sp.UpdateChaserFieldsAsync(t.SpItemId, important: true, incrementChase: false, nextChaseAtUtc: nextSendUtc, ct);
                }
                // We do NOT continue here; allow downstream logic to skip on missing due date, etc.
            }


            // Skip Teams notification for tasks without due dates - cannot determine overdue status
            // SharePoint tracking (nextChaseAtUtc) is already updated above for proper scheduling
            if (!enrichedCandidate.DueDateUtc.HasValue)
            {
                if (dryRunMode)
                {
                    _log.Warning("SkipNoDueDate: taskId={TaskId}, TaskName={TaskName}, CustomerId={CustomerId}, SpItemId={SpItemId} - " +
                                 "DueDateUtc is NULL, cannot send overdue notification. DRY RUN: Only SQLite nextChaseAtUtc cached; no SP write. " +
                                 "Task will be re-evaluated after date backfill.",
                        t.TaskId, t.TaskName, t.CustomerId, t.SpItemId);
                }
                else
                {
                    _log.Warning("SkipNoDueDate: taskId={TaskId}, TaskName={TaskName}, CustomerId={CustomerId}, SpItemId={SpItemId} - " +
                                 "DueDateUtc is NULL, cannot send overdue notification. SharePoint nextChaseAtUtc updated. " +
                                 "Task will be re-evaluated after date backfill.",
                        t.TaskId, t.TaskName, t.CustomerId, t.SpItemId);
                }
                continue; // Skip ACK link rotation, Teams notification, and chase count increment
            }


            // 4) rotate link
            var newVersion = (t.AckVersion <= 0 ? 1 : t.AckVersion) + 1;

            // Calculate next chase time (will be used for both link expiration and database updates)
            var nextUtc = BusinessDayHelper.NextBusinessDayAtHourUtc(t.Region, _opts.SendHourLocal, DateTimeOffset.UtcNow);

            // ACK link expires when next chase is due
            var expires = nextUtc;
            var ttl = expires - DateTimeOffset.UtcNow;

            var ackUrl = BuildAckUrl(t.TaskId, t.Region, t.AnchorDateType, expires, newVersion);
            _log.Debug("AckLinkRotate taskId={TaskId} oldVersion={Old} newVersion={New} ttlHours={Ttl} expUtc={Exp}",
                t.TaskId, t.AckVersion, newVersion, ttl.TotalHours, expires);

            if (dryRunMode)
            {
                _log.Information("DRY RUN: Would send Teams notification for TaskId={TaskId}, TaskName={TaskName}",
                    t.TaskId, t.TaskName);
                _log.Information("DRY RUN: Would update SP ItemId={SpItemId}", t.SpItemId);
                _log.Information("DRY RUN: Would update SQLite for TaskId={TaskId}", t.TaskId);
                continue; // Skip to next task without doing any updates
            }

            // 5) post to Teams thread (short text + card) with enriched context
            var companyDisplay = string.IsNullOrWhiteSpace(enrichedCandidate.CompanyName) ? "Unassigned Company" : enrichedCandidate.CompanyName;
            var phaseDisplay = string.IsNullOrWhiteSpace(enrichedCandidate.PhaseName) ? "—" : enrichedCandidate.PhaseName;
            var dueDisplay = enrichedCandidate.DueDateUtc.HasValue
                ? enrichedCandidate.DueDateUtc.Value.ToLocalTime().ToString("MMM d, yyyy")
                : "No due date";






            var overdueText = $"🔔 **{companyDisplay}** · Due: **{dueDisplay}** · Phase: *{phaseDisplay}*\n" +
                              $"OVERDUE: {enrichedCandidate.TaskName} needs attention. Please review and click the ACK Link when Completed.";

            bool proceedWithUpdates = false;

            if (_teams is TeamsNotificationService tsvc)
            {
                // New: send and capture IDs
                var (ok, rootId, lastId) = await tsvc.PostChaserWithIdsAsync(
                    t.TeamId,
                    t.ChannelId,
                    t.RootMessageId,
                    overdueText,
                    ackUrl,
                    _opts.ThreadFallback,
                    ct);


                _log.Information("TeamsPostResult taskId={TaskId} success={Success} rootId={RootId} lastId={LastId}",
                    t.TaskId, ok, rootId, lastId);

                if (ok)
                {
                    // Persist message IDs (non-DryRun path only; we're already past DryRun continue)
                    if (string.IsNullOrWhiteSpace(t.RootMessageId) && !string.IsNullOrWhiteSpace(rootId))
                    {
                        await SqliteQueries.UpdateRootMessageIdAsync(_dbPath, t.TaskId, rootId, _log, dryRun: false, ct);
                        await SqliteQueries.UpdateLastMessageIdAsync(_dbPath, t.TaskId, (lastId ?? rootId), _log, dryRun: false, ct);

                        _log.Information(
                            "Persisted first notification IDs for TaskId={TaskId}: RootMessageId='{RootId}', LastMessageId='{LastId}'",
                            t.TaskId, rootId, lastId ?? rootId
                        );
                    }
                    else if (!string.IsNullOrWhiteSpace(lastId))
                    {
                        await SqliteQueries.UpdateLastMessageIdAsync(_dbPath, t.TaskId, lastId, _log, dryRun: false, ct);

                        _log.Information(
                            "Persisted chaser LastMessageId for TaskId={TaskId}: LastMessageId='{LastId}'",
                            t.TaskId, lastId
                        );
                    }

                    proceedWithUpdates = true;
                }
                else
                {
                    _log.Error("TeamsPostFailed taskId={TaskId} (ID-capable path)", t.TaskId);
                }

            }
            else
            {
                // Backward-compatible fallback to existing interface
                var ok = await _teams.PostChaserAsync(t.TeamId, t.ChannelId, t.RootMessageId, overdueText, ackUrl, _opts.ThreadFallback, ct);
                _log.Information("TeamsPostResult taskId={TaskId} success={Success} (legacy path)", t.TaskId, ok);
                proceedWithUpdates = ok;
            }

            if (!proceedWithUpdates)
            {
                continue;
            }

            _log.Debug("Attempting SharePoint update for taskId={TaskId}", t.TaskId);







            // 6) write-through to SP (Important=true, ChaseCount+1, NextChaseAtUtc=nextUtc)
            await _sp.UpdateChaserFieldsAsync(t.SpItemId, important: true, incrementChase: true, nextChaseAtUtc: nextUtc, ct);
            _log.Information("SpUpdateSuccess taskId={TaskId} spItemId={SpItemId} nextChaseAtUtc={Next}", t.TaskId, t.SpItemId, nextUtc);

            _log.Debug("Attempting SQLite update for taskId={TaskId} newVersion={Version} expires={Expires}",
                t.TaskId, newVersion, expires);

            // 7) mirror to SQLite
            await SqliteQueries.UpdateChaserMirrorAsync(_dbPath, t.TaskId, newVersion, expires, nextUtc, _log, ct);
            _log.Information("SQLite update completed for taskId={TaskId}", t.TaskId);
        }
    }

    /// <summary>
    /// Implements sequential workflow dependency filtering.
    /// Groups tasks by (CustomerId, PhaseName, AnchorDateType) and ensures categories complete sequentially.
    /// Within a category, tasks at the same StartOffsetDays are parallel.
    /// </summary>
    private async Task<List<SqliteQueries.DueCandidate>> ApplyWorkflowDependencyFilterAsync(
        List<SqliteQueries.DueCandidate> allDueTasks,
        CancellationToken ct)
    {
        if (allDueTasks.Count == 0)
        {
            _log.Information("WorkflowFilter: No due tasks to filter");
            return allDueTasks;
        }

        _log.Information("WorkflowFilter: Applying sequential dependency filtering (category + earliest offset) to {Count} due tasks", allDueTasks.Count);

        var eligible = new List<SqliteQueries.DueCandidate>();

        // STRICT: drop orphans up front so they can't leak through
        var orphans = allDueTasks.Where(t =>
                string.IsNullOrWhiteSpace(t.CustomerId) ||
                string.IsNullOrWhiteSpace(t.PhaseName) ||
                string.IsNullOrWhiteSpace(t.CategoryKey) ||
                string.IsNullOrWhiteSpace(t.AnchorDateType) ||
                !t.StartOffsetDays.HasValue)
            .ToList();

        if (orphans.Count > 0)
        {
            _log.Warning("WorkflowFilter: STRICT block of {Count} task(s) missing required metadata (CustomerId/PhaseName/CategoryKey/AnchorDateType/StartOffsetDays).",
                orphans.Count);
        }

        var candidates = allDueTasks.Except(orphans).ToList();
        if (candidates.Count == 0)
        {
            _log.Information("WorkflowFilter: No candidates remain after strict metadata check");
            return eligible; // empty
        }

        // Correct scope: per Customer + Phase + AnchorDateType
        var groups = candidates
            .GroupBy(t => new { t.CustomerId, t.PhaseName, t.AnchorDateType })
            .ToList();

        _log.Information("WorkflowFilter: Found {GroupCount} customer/phase/anchor groups", groups.Count);

        var orderMap = _categoryOrderProvider.GetMap();

        foreach (var g in groups)
        {
            var customerId = g.Key.CustomerId!;
            var phaseName = g.Key.PhaseName!;
            var anchorDateType = g.Key.AnchorDateType!;

            _log.Debug("WorkflowFilter: Processing group - Customer={CustomerId}, Phase={PhaseName}, Anchor={AnchorDateType}, TaskCount={Count}",
                customerId, phaseName, anchorDateType, g.Count());

            // Cross-anchor dependency: HypercareEnd tasks cannot start until ALL GoLive tasks are complete
            if (anchorDateType.Equals("HypercareEnd", StringComparison.OrdinalIgnoreCase))
            {
                var goLiveComplete = await IsAnchorTypeCompleteAsync(customerId, phaseName, "GoLive", ct);
                if (!goLiveComplete)
                {
                    _log.Information("WorkflowFilter: HypercareEnd tasks for customer {CustomerId}, phase '{PhaseName}' are BLOCKED because GoLive workflow is not complete. Skipping {TaskCount} tasks.",
                        customerId, phaseName, g.Count());
                    continue; // Skip this entire anchor group
                }

                _log.Information("WorkflowFilter: GoLive workflow complete for customer {CustomerId}, phase '{PhaseName}'. HypercareEnd tasks are now eligible.",
                    customerId, phaseName);
            }

            // Determine earliest-open category for this (customer, phase, anchor)
            // Build list of (category, anchor) tuples from this group's tasks
            var distinctCategories = g.Select(t => (Category: t.CategoryKey!, AnchorDateType: t.AnchorDateType!))
                                      .Where(tuple => !string.IsNullOrWhiteSpace(tuple.Category) && !string.IsNullOrWhiteSpace(tuple.AnchorDateType))
                                      .Distinct()
                                      .OrderBy(tuple =>
                                      {
                                          if (!orderMap.TryGetValue(tuple, out var ord))
                                          {
                                              _log.Warning("WorkflowFilter: Unknown category/anchor '{Category}/{AnchorDateType}' for customer {CustomerId}, phase '{PhaseName}'. Treating as lowest priority.",
                                                  tuple.Category, tuple.AnchorDateType, customerId, phaseName);
                                              return int.MaxValue;
                                          }
                                          return ord;
                                      })
                                      .ToList();

            (string Category, string AnchorDateType)? earliestOpenCategory = null;
            foreach (var cat in distinctCategories)
            {
                var complete = await IsCategoryCompleteAsync(customerId, phaseName, cat.Category, cat.AnchorDateType, ct);
                if (!complete) { earliestOpenCategory = cat; break; }
            }

            if (earliestOpenCategory is null)
            {
                _log.Information("WorkflowFilter: All categories complete for customer {CustomerId}, phase '{PhaseName}', anchor {AnchorDateType}.",
                    customerId, phaseName, anchorDateType);
                continue;
            }

            // Bucket JUST the chosen category (anchor already filtered by grouping)
            var categoryBucket = g.Where(t => string.Equals(t.CategoryKey, earliestOpenCategory.Value.Category, StringComparison.OrdinalIgnoreCase))
                                  .ToList();

            // Determine earliest offset that still has any open task in this category
            var earliestOffset = await GetEarliestOpenOffsetAsync(customerId, phaseName, earliestOpenCategory.Value.Category, earliestOpenCategory.Value.AnchorDateType, ct); if (earliestOffset is null)
            {
                _log.Warning("WorkflowFilter: No open offset groups found for customer {CustomerId}, phase '{PhaseName}', category '{Category}'.",
                    customerId, phaseName, earliestOpenCategory.Value.Category);
                continue;
            }

            // Keep ONLY tasks in (earliest-open category, earliest-open offset)
            var kept = categoryBucket
                .Where(t => t.StartOffsetDays.HasValue && t.StartOffsetDays.Value == earliestOffset.Value)
                .ToList();

            // Log missing offsets in the chosen category (excluded)
            var missingOffsetInCategory = categoryBucket.Where(t => !t.StartOffsetDays.HasValue).Count();

            var skippedInCategory = categoryBucket.Count - kept.Count; // scoped to category, not whole group

            _log.Information("gate.pick customer={CustomerId} phase={PhaseName} anchor={AnchorDateType} category=\"{Category}\" offset={Offset} kept={Kept} skippedInCategory={Skipped} missingOffset={Missing}",
                customerId, phaseName, anchorDateType, earliestOpenCategory.Value.Category, earliestOffset, kept.Count, skippedInCategory, missingOffsetInCategory);

            eligible.AddRange(kept);
        }

        _log.Information("WorkflowFilter: Filtering complete. Original={Original}, Eligible={Eligible}",
            allDueTasks.Count, eligible.Count);

        return eligible;
    }


    /// <summary>
    /// Processes workflow dependencies for a single customer's category workflow stream.
    /// Enforces: categories are sequential; tasks within a category are parallel.
    /// Returns all due tasks in this category only if it is the earliest incomplete category
    /// for the (CustomerId, PhaseName) group; otherwise returns an empty list.
    /// </summary>
    private async Task<List<SqliteQueries.DueCandidate>> ProcessCustomerCategoryWorkflowAsync(
        string customerId,
        string? categoryKey,
        string phaseName,
        string anchorDateType, // retained for signature compatibility; not used for gating
        List<SqliteQueries.DueCandidate> categoryTasks,
        CancellationToken ct)
    {
        // Handle null/blank category during transition
        if (string.IsNullOrWhiteSpace(categoryKey))
        {
            _log.Warning("WorkflowFilter: Customer {CustomerId} has tasks with null CategoryKey. Allowing during transition period.",
                customerId);
            return categoryTasks;
        }

        _log.Debug("WorkflowFilter: Processing category '{CategoryKey}' for customer {CustomerId}, phase '{PhaseName}' with {TaskCount} due tasks",
            categoryKey, customerId, phaseName, categoryTasks.Count);

        // Gate: only proceed if this is the earliest incomplete category for (customer, phase)
        var isEarliest = await IsEarliestIncompleteCategoryAsync(customerId, phaseName, categoryKey, anchorDateType, ct);
        if (!isEarliest)
        {
            _log.Information(
                "WorkflowFilter: Category '{CategoryKey}' for customer {CustomerId}, phase '{PhaseName}' is blocked by earlier incomplete categories. Skipping {TaskCount} due tasks.",
                categoryKey, customerId, phaseName, categoryTasks.Count);
            return new List<SqliteQueries.DueCandidate>();
        }

        // Parallel within category: return all due tasks for this category
        _log.Debug("WorkflowFilter: Category '{CategoryKey}' is earliest-open for customer {CustomerId}, phase '{PhaseName}'. Eligible={Count}",
            categoryKey, customerId, phaseName, categoryTasks.Count);

        return categoryTasks;
    }

    /// <summary>
    /// Determines if the given category is the earliest incomplete category for the customer.
    /// This enforces sequential workflow progression: Category A must complete before Category B can start.
    /// </summary>
    private async Task<bool> IsEarliestIncompleteCategoryAsync(
        string customerId,
        string phaseName,
        string categoryKey,
        string anchorDateType,
        CancellationToken ct)
    {
        // This is a simplified implementation that needs to be enhanced with proper category ordering logic
        // For now, we'll use a known workflow order based on the template

        var workflowCategoryOrder = new[]
        {
        "Support Transition Packet Delivered - T-4 weeks",
        "Support Packet Responded To",
        "Gates to meeting",
        "Transition Discussion Meeting",
        "Transition Acceptance",
        "Customer Instructions and Introductions",
        "Support Activities"
    };

        var currentCategoryIndex = Array.IndexOf(workflowCategoryOrder, categoryKey);
        if (currentCategoryIndex == -1)
        {
            _log.Warning("WorkflowFilter: Unknown category '{CategoryKey}' not found in workflow order. Allowing to proceed.",
                categoryKey);
            return true; // Unknown categories are allowed to proceed
        }

        // Check all earlier categories to see if any are incomplete
        for (int i = 0; i < currentCategoryIndex; i++)
        {
            var earlierCategory = workflowCategoryOrder[i];
            var isEarlierCategoryComplete = await IsCategoryCompleteAsync(customerId, phaseName, earlierCategory, anchorDateType, ct);

            if (!isEarlierCategoryComplete)
            {
                _log.Information("WorkflowFilter: Category '{CategoryKey}' is blocked by incomplete earlier category '{EarlierCategory}' for customer {CustomerId}",
                    categoryKey, earlierCategory, customerId);
                return false;
            }
        }

        _log.Information("WorkflowFilter: Category '{CategoryKey}' is the earliest incomplete category for customer {CustomerId}. Allowed to proceed.",
            categoryKey, customerId);
        return true;
    }

    private string BuildAckUrl(long taskId, string? region, string? anchorDateType, DateTimeOffset expiresUtc, int ackVersion)
    {
        var baseUrl = (_signerOpts.BaseUrl ??
                       Environment.GetEnvironmentVariable("AckLink__BaseUrl") ??
                       "https://localhost").TrimEnd('/');

        var expUnix = expiresUtc.ToUnixTimeSeconds();

        // Build payload that matches what AckActionHandler expects
        var payload = $"id={taskId}&exp={expUnix}";
        var sig = _signer.Sign(payload);

        return $"{baseUrl}/maintenance/actions/mark-complete?id={taskId}&exp={expUnix}&sig={Uri.EscapeDataString(sig)}";
    }

    private static string ExtractSqlitePathOrFallback(string? connectionString, string contentRootPath)
    {
        // Try to parse a Data Source / DataSource / Filename from the connection string
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2) continue;
                var key = kv[0];
                var val = kv[1];
                if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Filename", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("FileName", StringComparison.OrdinalIgnoreCase))
                {
                    return val;
                }
            }
        }

        // Fallback: packaged DB under ./DB (matches your EF registration fallback)
        var fallbackPath = Path.Combine(contentRootPath, "DB", "ConfluenceSyncServiceDB.db");
        return fallbackPath;
    }

    /// <summary>
    /// DEPRECATED: Old synchronous filtering method. Kept for reference during migration.
    /// </summary>
    private List<SqliteQueries.DueCandidate> ApplyWorkflowDependencyFilter(List<SqliteQueries.DueCandidate> allDueTasks)
    {
        _log.Warning("Using deprecated ApplyWorkflowDependencyFilter. Should migrate to async version.");

        // Check if any "Gentle Chaser - PM Ensure Prepared" tasks exist
        var pmEnsurePreparedTasks = allDueTasks
            .Where(t => t.TaskName.Contains("Gentle Chaser - PM Ensure Prepared", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pmEnsurePreparedTasks.Any())
        {
            _log.Information("WorkflowFilter: Found {Count} PM Ensure Prepared tasks. Blocking all other tasks.", pmEnsurePreparedTasks.Count);
            return pmEnsurePreparedTasks; // Only process PM tasks, block everything else
        }

        _log.Information("WorkflowFilter: No PM Ensure Prepared tasks found. Processing all {Count} due tasks.", allDueTasks.Count);
        return allDueTasks; // No PM tasks due, process everything normally
    }

    #region Helper Classes
    /// <summary>
    /// Checks if all tasks in a category are completed for the given customer.
    /// </summary>
    private async Task<bool> IsCategoryCompleteAsync(
    string customerId,
    string phaseName,
    string categoryKey,
    string anchorDateType,
    CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM TaskIdMap
WHERE CustomerId    = $customerId
  AND PhaseName     = $phaseName
  AND Category_Key  = $categoryKey
  AND AnchorDateType = $anchorDateType
  AND State         = 'linked'
  AND (Status IS NULL OR Status <> 'Completed');";

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$customerId", customerId);
        cmd.Parameters.AddWithValue("$phaseName", phaseName);
        cmd.Parameters.AddWithValue("$categoryKey", categoryKey);
        cmd.Parameters.AddWithValue("$anchorDateType", anchorDateType);

        var remaining = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        var isComplete = remaining == 0;

        _log.Debug("WorkflowFilter: CategoryComplete? customer={CustomerId} phase='{PhaseName}' category='{CategoryKey}'" +
            " anchor={AnchorDateType} remainingOpen={Remaining} => {Complete}", customerId, phaseName, categoryKey, anchorDateType,
            remaining, isComplete);

        return isComplete;
    }

    private async Task<int?> GetEarliestOpenOffsetAsync(
        string customerId,
        string phaseName,
        string categoryKey,
        string anchorDateType,
        CancellationToken ct)
    {
        // Find the lowest StartOffsetDays in this (customer, phase, category, anchor) that still has any NOT Completed tasks.
        const string sql = @"
SELECT MIN(StartOffsetDays)
FROM TaskIdMap
WHERE CustomerId    = $customerId
  AND PhaseName     = $phaseName
  AND Category_Key  = $categoryKey
  AND AnchorDateType = $anchorDateType
  AND State         = 'linked'
  AND (Status IS NULL OR Status <> 'Completed');";

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$customerId", customerId);
        cmd.Parameters.AddWithValue("$phaseName", phaseName);
        cmd.Parameters.AddWithValue("$categoryKey", categoryKey);
        cmd.Parameters.AddWithValue("$anchorDateType", anchorDateType);

        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj is DBNull or null) return null;

        // SQLite stores ints as long; convert carefully
        if (obj is long l) return unchecked((int)l);
        if (int.TryParse(obj.ToString(), out var i)) return i;

        return null;
    }

    /// <summary>
    /// Checks if all tasks for a specific anchor type are completed for the given customer/phase.
    /// Used to enforce cross-anchor dependencies (e.g., HypercareEnd blocked until GoLive complete).
    /// </summary>
    private async Task<bool> IsAnchorTypeCompleteAsync(
        string customerId,
        string phaseName,
        string anchorDateType,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM TaskIdMap
WHERE CustomerId = $customerId
  AND PhaseName = $phaseName
  AND AnchorDateType = $anchorDateType
  AND State = 'linked'
  AND (Status IS NULL OR Status <> 'Completed');";

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$customerId", customerId);
        cmd.Parameters.AddWithValue("$phaseName", phaseName);
        cmd.Parameters.AddWithValue("$anchorDateType", anchorDateType);

        var remaining = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        var isComplete = remaining == 0;

        _log.Debug("WorkflowFilter: AnchorTypeComplete? customer={CustomerId} phase='{PhaseName}' anchor={AnchorDateType} remainingOpen={Remaining} => {Complete}",
            customerId, phaseName, anchorDateType, remaining, isComplete);

        return isComplete;
    }


    #endregion
}
