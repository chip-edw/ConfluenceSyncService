using ConfluenceSyncService.Data;
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

    private int _consecutiveFailures;

    public ChaserJobHostedService(
        Serilog.ILogger log,
        IOptions<ChaserJobOptions> opts,
        SharePointClient sp,
        INotificationService teams,
        IHmacSigner signer,
        IOptions<AckSignerOptions> signerOpts,
        IOptions<AckLinkOptions> ackPolicyOpts,
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
    /// Groups tasks by (CustomerId, Category_Key, AnchorDateType) and ensures categories complete sequentially.
    /// FIXED: Now uses Category_Key instead of StartOffsetDays for proper workflow sequencing.
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

        _log.Information("WorkflowFilter: Applying sequential dependency filtering to {Count} due tasks", allDueTasks.Count);

        var eligibleTasks = new List<SqliteQueries.DueCandidate>();

        // FIXED: Group by customer, category, and anchor date type for proper sequential workflow enforcement
        var customerGroups = allDueTasks
            .Where(t => !string.IsNullOrWhiteSpace(t.CustomerId))
            .GroupBy(t => new { t.CustomerId, t.CategoryKey, t.AnchorDateType })  // PROPER SEQUENCING
            .ToList();

        _log.Information("WorkflowFilter: Found {GroupCount} customer workflow streams (by Category)", customerGroups.Count());

        foreach (var customerGroup in customerGroups)
        {
            var key = customerGroup.Key;
            var customerTasks = customerGroup.ToList();

            _log.Debug("WorkflowFilter: Processing customer {CustomerId}, category '{CategoryKey}', anchor {AnchorType} with {TaskCount} due tasks",
                key.CustomerId, key.CategoryKey ?? "(null)", key.AnchorDateType, customerTasks.Count);

            try
            {
                var eligibleForCustomer = await ProcessCustomerCategoryWorkflowAsync(
                    key.CustomerId,
                    key.CategoryKey,
                    key.AnchorDateType,
                    customerTasks,
                    ct);

                eligibleTasks.AddRange(eligibleForCustomer);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "WorkflowFilter: Error processing workflow for customer {CustomerId}, category '{CategoryKey}', anchor {AnchorType}. Skipping this group.",
                    key.CustomerId, key.CategoryKey ?? "(null)", key.AnchorDateType);
            }
        }

        // Handle tasks with missing workflow metadata (transition period support)
        var orphanedTasks = allDueTasks
            .Where(t => string.IsNullOrWhiteSpace(t.CustomerId) ||
                       string.IsNullOrWhiteSpace(t.CategoryKey) ||
                       !t.StartOffsetDays.HasValue)
            .ToList();

        if (orphanedTasks.Count > 0)
        {
            _log.Warning("WorkflowFilter: Found {Count} tasks with missing workflow metadata (CustomerId, CategoryKey, or StartOffsetDays). " +
                        "Adding to eligible tasks without dependency checking for transition period support.",
                orphanedTasks.Count);
            eligibleTasks.AddRange(orphanedTasks);
        }

        _log.Information("WorkflowFilter: Sequential filtering complete. Original={Original}, Eligible={Eligible}",
            allDueTasks.Count, eligibleTasks.Count);

        return eligibleTasks;
    }

    /// <summary>
    /// Processes workflow dependencies for a single customer's category workflow stream.
    /// FIXED: Now enforces sequential category completion before allowing next category to proceed.
    /// </summary>
    private async Task<List<SqliteQueries.DueCandidate>> ProcessCustomerCategoryWorkflowAsync(
        string customerId,
        string? categoryKey,
        string anchorDateType,
        List<SqliteQueries.DueCandidate> categoryTasks,
        CancellationToken ct)
    {
        // Handle null CategoryKey (transition period)
        if (string.IsNullOrWhiteSpace(categoryKey))
        {
            _log.Warning("WorkflowFilter: Customer {CustomerId} has tasks with null CategoryKey. Allowing them to proceed for transition period.",
                customerId);
            return categoryTasks;
        }

        _log.Debug("WorkflowFilter: Processing category '{CategoryKey}' for customer {CustomerId} with {TaskCount} due tasks",
            categoryKey, customerId, categoryTasks.Count);

        // Check if this category is the earliest incomplete category for this customer
        var isEarliestIncompleteCategory = await IsEarliestIncompleteCategoryAsync(
            customerId, categoryKey, anchorDateType, ct);

        if (!isEarliestIncompleteCategory)
        {
            _log.Information("WorkflowFilter: Category '{CategoryKey}' for customer {CustomerId} is blocked by earlier incomplete categories. " +
                            "Skipping {TaskCount} due tasks.", categoryKey, customerId, categoryTasks.Count);
            return new List<SqliteQueries.DueCandidate>();
        }

        // Within the category, group by StartOffsetDays for parallel task support
        var offsetGroups = categoryTasks
            .Where(t => t.StartOffsetDays.HasValue)
            .GroupBy(t => t.StartOffsetDays!.Value)
            .OrderBy(g => g.Key) // Sequential order within category: earliest offset first
            .ToList();

        if (offsetGroups.Count == 0)
        {
            _log.Warning("WorkflowFilter: Category '{CategoryKey}' for customer {CustomerId} has no tasks with valid StartOffsetDays",
                categoryKey, customerId);
            return new List<SqliteQueries.DueCandidate>();
        }

        _log.Debug("WorkflowFilter: Category '{CategoryKey}' has {GroupCount} offset groups: [{Groups}]",
            categoryKey, offsetGroups.Count, string.Join(", ", offsetGroups.Select(g => $"Day {g.Key}")));

        // Within the category, process offset groups sequentially
        foreach (var group in offsetGroups)
        {
            var offsetDays = group.Key;
            var groupTasks = group.ToList();

            _log.Debug("WorkflowFilter: Checking offset group Day {OffsetDays} in category '{CategoryKey}' with {TaskCount} due tasks",
                offsetDays, categoryKey, groupTasks.Count);

            // Check if ALL tasks in this offset group are completed
            var groupStatus = await SqliteQueries.GetGroupTaskStatusAsync(
                _dbPath, customerId, categoryKey!, anchorDateType, offsetDays, _log, ct);

            var completedTasks = groupStatus.Count(t =>
                string.Equals(t.Status, "Completed", StringComparison.OrdinalIgnoreCase));

            var totalTasksInGroup = groupStatus.Count;

            _log.Debug("WorkflowFilter: Offset group Day {OffsetDays} in category '{CategoryKey}' status: {Completed}/{Total} tasks completed",
                offsetDays, categoryKey, completedTasks, totalTasksInGroup);

            // If this offset group is incomplete, return only "Not Started" tasks from this group and block later groups
            if (completedTasks < totalTasksInGroup)
            {
                // Build a map of TaskId -> Status for this offset group (from SQLite)
                var statusMap = groupStatus.ToDictionary(
                    t => t.TaskId,
                    t => (t.Status ?? "").Trim(),
                    comparer: EqualityComparer<long>.Default
                );

                // Keep only tasks whose DB status is exactly "Not Started"
                var notStartedTasks = groupTasks
                    .Where(t =>
                        statusMap.TryGetValue(t.TaskId, out var s) &&
                        string.Equals(s, "Not Started", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                _log.Information(
                    "WorkflowFilter: Incomplete offset group Day {OffsetDays} in category '{CategoryKey}' for customer {CustomerId}. " +
                    "Eligible (Not Started)={EligibleCount} of Due={DueCount}. Blocking later offset groups.",
                    offsetDays, categoryKey, customerId, notStartedTasks.Count, groupTasks.Count
                );

                // If none are Not Started, block later groups by returning an empty set
                return notStartedTasks;
            }

            else
            {
                _log.Information("WorkflowFilter: Offset group Day {OffsetDays} in category '{CategoryKey}' for customer {CustomerId} is complete ({Completed}/{Total}). " +
                                "Checking next offset group.", offsetDays, categoryKey, customerId, completedTasks, totalTasksInGroup);
            }
        }

        // All offset groups in this category are complete
        _log.Information("WorkflowFilter: All offset groups in category '{CategoryKey}' are complete for customer {CustomerId}. No eligible tasks.",
            categoryKey, customerId);
        return new List<SqliteQueries.DueCandidate>();
    }

    /// <summary>
    /// Determines if the given category is the earliest incomplete category for the customer.
    /// This enforces sequential workflow progression: Category A must complete before Category B can start.
    /// </summary>
    private async Task<bool> IsEarliestIncompleteCategoryAsync(
        string customerId,
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
            var isEarlierCategoryComplete = await IsCategoryCompleteAsync(customerId, earlierCategory, anchorDateType, ct);

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
    #region  Helper Classes
    /// <summary>
    /// Checks if all tasks in a category are completed for the given customer.
    /// </summary>
    private async Task<bool> IsCategoryCompleteAsync(
        string customerId,
        string categoryKey,
        string anchorDateType,
        CancellationToken ct)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        SELECT COUNT(*) as Total,
               SUM(CASE WHEN IFNULL(Status, '') = 'Completed' THEN 1 ELSE 0 END) as Completed
        FROM TaskIdMap
        WHERE CustomerId = $customerId
          AND Category_Key = $categoryKey
          AND AnchorDateType = $anchorDateType
          AND State = 'linked'";

        cmd.Parameters.AddWithValue("$customerId", customerId);
        cmd.Parameters.AddWithValue("$categoryKey", categoryKey);
        cmd.Parameters.AddWithValue("$anchorDateType", anchorDateType);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var total = reader.GetInt32("Total");
            var completed = reader.GetInt32("Completed");

            var isComplete = total > 0 && completed == total;

            _log.Debug("WorkflowFilter: Category '{CategoryKey}' completion check for customer {CustomerId}: {Completed}/{Total} (Complete: {IsComplete})",
                categoryKey, customerId, completed, total, isComplete);

            return isComplete;
        }

        _log.Debug("WorkflowFilter: No tasks found for category '{CategoryKey}' and customer {CustomerId}. Treating as complete.",
            categoryKey, customerId);
        return true; // No tasks = complete
    }
    #endregion
}
