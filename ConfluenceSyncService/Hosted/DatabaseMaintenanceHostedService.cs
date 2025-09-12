using ConfluenceSyncService.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ConfluenceSyncService.Hosted;

public sealed class DatabaseMaintenanceHostedService(
    IOptions<DatabaseMaintenanceOptions> options,
    IConfiguration config,
    IHostEnvironment env,
    ILogger<DatabaseMaintenanceHostedService> logger)
    : BackgroundService
{
    private readonly DatabaseMaintenanceOptions _options = options.Value;
    private readonly IConfiguration _config = config;
    private readonly IHostEnvironment _env = env;
    private readonly ILogger<DatabaseMaintenanceHostedService> _log = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CheckpointEnabled)
        {
            _log.LogInformation("Database maintenance disabled via configuration");
            return;
        }

        _log.LogInformation("Database maintenance service started. Interval: {IntervalHours} hours, Mode: {Mode}",
            _options.CheckpointIntervalHours, _options.CheckpointMode);

        var interval = TimeSpan.FromHours(_options.CheckpointIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await PerformMaintenanceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("Database maintenance service stopping");
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Database maintenance failed");
            }
        }
    }

    private async Task PerformMaintenanceAsync(CancellationToken ct)
    {
        // Use same connection string resolution as ApplicationDbContext
        var cs = _config.GetConnectionString("ConfluenceSync");
        if (string.IsNullOrWhiteSpace(cs))
        {
            var fallbackPath = Path.Combine(_env.ContentRootPath, "DB", "ConfluenceSyncServiceDB.db");
            cs = $"Data Source={fallbackPath};Cache=Shared";
        }

        _log.LogInformation("Starting periodic database maintenance");
        var startTime = DateTime.UtcNow;

        try
        {
            using var connection = new SqliteConnection(cs);
            await connection.OpenAsync(ct);

            var checkpointCommand = $"PRAGMA wal_checkpoint({_options.CheckpointMode})";
            using var command = new SqliteCommand(checkpointCommand, connection);
            await command.ExecuteNonQueryAsync(ct);

            var duration = DateTime.UtcNow - startTime;
            _log.LogInformation("Periodic database maintenance completed in {Duration:mm\\:ss}", duration);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to perform periodic database maintenance");
            throw;
        }
    }
}
