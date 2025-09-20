using ConfluenceSyncService.Data;
using ConfluenceSyncService.Options;
using ConfluenceSyncService.Security; // IHmacSigner, AckSignerOptions
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Teams;
using ConfluenceSyncService.Time;
using ConfluenceSyncService.Utilities;
using Microsoft.Extensions.Options;

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

            // 2) SP confirm by item id: Status + DueDateUtc
            var statusDue = await _sp.GetTaskStatusAndDueUtcAsync(t.SpItemId, ct);
            if (statusDue is null)
            {
                _log.Warning("SpConfirmStatus: missing itemId={SpItemId}", t.SpItemId);
                continue;
            }
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
            //if (!inWindow)
            //{
            //    await SqliteQueries.UpdateNextChaseCachedAsync(_dbPath, t.TaskId, nextSendUtc, _log, ct);
            //    // write-through to SP to keep Power BI truth
            await _sp.UpdateChaserFieldsAsync(t.SpItemId, important: true, incrementChase: false, nextChaseAtUtc: nextSendUtc, ct);
            //    continue;
            //}

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

            // 5) post to Teams thread (short text + card)
            var overdueText = $"OVERDUE: {t.TaskName} was due {statusDue.DueDateUtc?.ToLocalTime():g}. Please review and ACK.";

            var postOk = await _teams.PostChaserAsync(t.TeamId, t.ChannelId, t.RootMessageId, overdueText, ackUrl, _opts.ThreadFallback, ct);
            _log.Information("TeamsPostResult taskId={TaskId} success={Success}", t.TaskId, postOk);

            // TEMPORARY: Treat root message success as overall success to test database updates
            bool proceedWithUpdates = true; // Force true to test database logic

            if (!proceedWithUpdates) // Changed from !postOk
            {
                _log.Error("TeamsPostFailed taskId={TaskId}", t.TaskId);
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
    /// Groups tasks by (CustomerId, AnchorDateType, StartOffsetDays) and ensures groups complete sequentially.
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

        // Group by customer and anchor date type for independent workflow streams
        var customerGroups = allDueTasks
            .Where(t => !string.IsNullOrWhiteSpace(t.CustomerId))
            .GroupBy(t => new { t.CustomerId, t.AnchorDateType })
            .ToList();

        _log.Information("WorkflowFilter: Found {GroupCount} customer workflow streams", customerGroups.Count());

        foreach (var customerGroup in customerGroups)
        {
            var key = customerGroup.Key;
            var customerTasks = customerGroup.ToList();

            _log.Debug("WorkflowFilter: Processing customer {CustomerId}, anchor {AnchorType} with {TaskCount} due tasks",
                key.CustomerId, key.AnchorDateType, customerTasks.Count);

            try
            {
                var eligibleForCustomer = await ProcessCustomerWorkflowAsync(
                    key.CustomerId,
                    key.AnchorDateType,
                    customerTasks,
                    ct);

                eligibleTasks.AddRange(eligibleForCustomer);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "WorkflowFilter: Error processing workflow for customer {CustomerId}, anchor {AnchorType}. Skipping this customer.",
                    key.CustomerId, key.AnchorDateType);
            }
        }

        // Handle tasks with missing CustomerId or StartOffsetDays (fallback to old behavior for these)
        var orphanedTasks = allDueTasks
            .Where(t => string.IsNullOrWhiteSpace(t.CustomerId) || !t.StartOffsetDays.HasValue)
            .ToList();

        if (orphanedTasks.Count > 0)
        {
            _log.Warning("WorkflowFilter: Found {Count} tasks with missing workflow metadata. Adding to eligible tasks without dependency checking.",
                orphanedTasks.Count);
            eligibleTasks.AddRange(orphanedTasks);
        }

        _log.Information("WorkflowFilter: Sequential filtering complete. Original={Original}, Eligible={Eligible}",
            allDueTasks.Count, eligibleTasks.Count);

        return eligibleTasks;
    }

    /// <summary>
    /// Processes workflow dependencies for a single customer's workflow stream.
    /// Returns only tasks from the earliest incomplete group that is eligible to run.
    /// </summary>
    private async Task<List<SqliteQueries.DueCandidate>> ProcessCustomerWorkflowAsync(
        string customerId,
        string anchorDateType,
        List<SqliteQueries.DueCandidate> customerTasks,
        CancellationToken ct)
    {
        // Group tasks by StartOffsetDays (workflow groups)
        var offsetGroups = customerTasks
            .Where(t => t.StartOffsetDays.HasValue)
            .GroupBy(t => t.StartOffsetDays!.Value)
            .OrderBy(g => g.Key) // Sequential order: earliest offset first
            .ToList();

        if (offsetGroups.Count == 0)
        {
            _log.Warning("WorkflowFilter: Customer {CustomerId} has no tasks with valid StartOffsetDays", customerId);
            return new List<SqliteQueries.DueCandidate>();
        }

        _log.Debug("WorkflowFilter: Customer {CustomerId} has {GroupCount} workflow groups: [{Groups}]",
            customerId, offsetGroups.Count, string.Join(", ", offsetGroups.Select(g => $"Day {g.Key}")));

        // Process groups sequentially - find first incomplete group
        foreach (var group in offsetGroups)
        {
            var offsetDays = group.Key;
            var groupTasks = group.ToList();

            _log.Debug("WorkflowFilter: Checking group Day {OffsetDays} with {TaskCount} due tasks",
                offsetDays, groupTasks.Count);

            // Check if ALL tasks in this group are completed
            var groupStatus = await SqliteQueries.GetGroupTaskStatusAsync(
                _dbPath, customerId, anchorDateType, offsetDays, _log, ct);

            var completedTasks = groupStatus.Count(t =>
                string.Equals(t.Status, "Completed", StringComparison.OrdinalIgnoreCase));

            var totalTasksInGroup = groupStatus.Count;

            _log.Debug("WorkflowFilter: Group Day {OffsetDays} status: {Completed}/{Total} tasks completed",
                offsetDays, completedTasks, totalTasksInGroup);

            // If the group is incomplete, check if it's eligible to run
            if (completedTasks < totalTasksInGroup)
            {
                _log.Information("WorkflowFilter: Found incomplete group Day {OffsetDays} for customer {CustomerId}. " +
                    "Group has {Due} due tasks. This blocks all subsequent groups.",
                    offsetDays, customerId, groupTasks.Count);

                // Return only the due tasks from this group (first incomplete group)
                return groupTasks;
            }
            else
            {
                _log.Information("WorkflowFilter: Group Day {OffsetDays} for customer {CustomerId} is complete ({Completed}/{Total}). " +
                    "Checking next group.", offsetDays, customerId, completedTasks, totalTasksInGroup);
            }
        }

        // All groups are complete - no tasks are eligible (workflow finished)
        _log.Information("WorkflowFilter: All workflow groups complete for customer {CustomerId}. No eligible tasks.",
            customerId);
        return new List<SqliteQueries.DueCandidate>();
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
}
