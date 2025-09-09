using ConfluenceSyncService.Data;
using ConfluenceSyncService.Options;
using ConfluenceSyncService.Security; // IHmacSigner, AckSignerOptions
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Teams;
using ConfluenceSyncService.Time;
using ConfluenceSyncService.Utilities;
using Microsoft.Extensions.Options;
using Serilog;

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
        // 1) fetch candidates from SQLite cache
        var due = await SqliteQueries.GetDueChaserCandidatesAsync(_dbPath, _opts.BatchSize, _log, ct);
        _log.Information("SqliteCandidateFetch count={Count}", due.Count);
        if (due.Count == 0) return;

        foreach (var t in due)
        {
            Log.Information("Processing task: TaskId={TaskId}, TeamId={TeamId}, ChannelId={ChannelId}, RootMessageId={RootMessageId}," +
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
                _log.Information("SkipCompleted taskId={TaskId}", t.TaskId);
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
            _log.Information("ChaserWindowCheck taskId={TaskId} inWindow={InWindow} nextSendUtc={Next}", t.TaskId, inWindow, nextSendUtc);
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
            _log.Information("AckLinkRotate taskId={TaskId} oldVersion={Old} newVersion={New} ttlHours={Ttl} expUtc={Exp}",
                t.TaskId, t.AckVersion, newVersion, ttl.TotalHours, expires);

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

            _log.Information("Attempting SharePoint update for taskId={TaskId}", t.TaskId);

            // 6) write-through to SP (Important=true, ChaseCount+1, NextChaseAtUtc=nextUtc)
            await _sp.UpdateChaserFieldsAsync(t.SpItemId, important: true, incrementChase: true, nextChaseAtUtc: nextUtc, ct);
            _log.Information("SpUpdateSuccess taskId={TaskId} spItemId={SpItemId} nextChaseAtUtc={Next}", t.TaskId, t.SpItemId, nextUtc);

            _log.Information("Attempting SQLite update for taskId={TaskId} newVersion={Version} expires={Expires}",
                t.TaskId, newVersion, expires);

            // 7) mirror to SQLite
            await SqliteQueries.UpdateChaserMirrorAsync(_dbPath, t.TaskId, newVersion, expires, nextUtc, _log, ct);
            _log.Information("SQLite update completed for taskId={TaskId}", t.TaskId);
        }
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
}
