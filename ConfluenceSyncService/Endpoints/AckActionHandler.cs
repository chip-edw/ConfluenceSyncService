using ConfluenceSyncService.Identity;
using ConfluenceSyncService.Options;
using ConfluenceSyncService.Security;
using ConfluenceSyncService.SharePoint;
using ConfluenceSyncService.Teams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ConfluenceSyncService.Endpoints
{
    public sealed class AckActionHandler(
        IHmacSigner signer,
        IClickerIdentityProvider identityProvider,
        ISharePointTaskUpdater sp,
        IOptions<AckLinkOptions> ackOpts,
        ILogger<AckActionHandler> log,
        IConfiguration config,
        IHostEnvironment env,
        INotificationService teamsNotificationService)
    {
        private readonly string _dbPath = ExtractSqlitePathOrFallback(
            config.GetConnectionString("ConfluenceSync"),
            env.ContentRootPath);
        private readonly INotificationService _teamsNotificationService = teamsNotificationService;

        public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
        {
            var q = ctx.Request.Query;
            var id = q["id"].ToString();
            var exp = long.TryParse(q["exp"], out var e) ? e : 0;
            var sig = q["sig"].ToString();
            var corr = q["c"].ToString();
            var listId = q["list"].ToString();

            if (string.IsNullOrWhiteSpace(id) || exp == 0 || string.IsNullOrWhiteSpace(sig))
                return Results.BadRequest("Missing required parameters.");

            var data = $"id={id}&exp={exp}" + (string.IsNullOrEmpty(corr) ? "" : $"&c={corr}");
            if (!signer.Verify(data, sig)) return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > exp) return Results.StatusCode(StatusCodes.Status410Gone);

            var who = await identityProvider.GetIdentityAsync(ctx, ct);
            var ackBy = who?.DisplayName ?? "unknown";
            var ackActual = who?.Email ?? who?.Upn;

            try
            {
                // 1. Update SharePoint (source of truth)
                var ok = await sp.MarkCompletedAsync(listId, id, ackBy, ackActual, ct);

                if (!ok)
                {
                    log.LogWarning("MarkCompleted returned false for item {ItemId}", id);
                }
                else
                {
                    // 2. Update SQLite cache to match SharePoint
                    // The 'id' parameter is the TaskId
                    if (long.TryParse(id, out var taskId))
                    {
                        try
                        {
                            await UpdateCacheStatusAsync(taskId, Models.TaskStatus.Completed, ct);
                            log.LogInformation("ACK: Updated SQLite cache Status='Completed' for TaskId={TaskId}", taskId);

                            // 3. Update Teams messages to show acknowledgment
                            try
                            {
                                var (teamId, channelId, rootMessageId, lastMessageId) = await GetTeamsMessageIdsAsync(taskId, ct);

                                if (!string.IsNullOrWhiteSpace(teamId) &&
                                    !string.IsNullOrWhiteSpace(channelId) &&
                                    !string.IsNullOrWhiteSpace(lastMessageId))
                                {
                                    var acknowledgedAt = DateTimeOffset.UtcNow;
                                    var updateSuccess = await teamsNotificationService.UpdateMessageAsAcknowledgedAsync(
                                        teamId,
                                        channelId,
                                        lastMessageId,
                                        ackBy,
                                        acknowledgedAt,
                                        ct);

                                    if (updateSuccess)
                                    {
                                        log.LogInformation("ACK: Successfully updated Teams message {MessageId} for TaskId={TaskId}",
                                            lastMessageId, taskId);
                                    }
                                    else
                                    {
                                        log.LogWarning("ACK: Failed to update Teams message {MessageId} for TaskId={TaskId}",
                                            lastMessageId, taskId);
                                    }
                                }
                                else
                                {
                                    log.LogInformation("ACK: No Teams message IDs found for TaskId={TaskId}, skipping message update",
                                        taskId);
                                }
                            }
                            catch (Exception teamsEx)
                            {
                                log.LogError(teamsEx, "ACK: Exception updating Teams message for TaskId={TaskId}. ACK still successful.",
                                    taskId);
                                // Don't fail the ACK - SharePoint and cache are updated
                            }
                        }
                        catch (Exception cacheEx)
                        {
                            log.LogError(cacheEx, "ACK: Failed to update SQLite cache for TaskId={TaskId}. Cache now stale!", taskId);
                            // Don't fail the ACK - SharePoint is updated, cache will heal eventually
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to mark complete for item {ItemId}", id);
                // Still return 200 to keep clicker UX resilient
            }

            return Results.Text("Acknowledged. You can close this window.");
        }

        /// <summary>
        /// Updates the SQLite cache Status field for a task identified by TaskId.
        /// This keeps the cache in sync with SharePoint after ACK.
        /// </summary>
        private async Task UpdateCacheStatusAsync(long taskId, string status, CancellationToken ct)
        {
            const string sql = @"
        UPDATE TaskIdMap
        SET Status = $status
        WHERE TaskId = $taskId;";

            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$taskId", taskId);  // Changed parameter name

            var rows = await cmd.ExecuteNonQueryAsync(ct);

            if (rows == 0)
            {
                log.LogWarning("ACK cache update: 0 rows affected for TaskId={TaskId}. Task may not exist in cache.", taskId);
            }
            else
            {
                log.LogInformation("ACK cache update: Updated {Rows} row(s) for TaskId={TaskId} to Status='{Status}'", rows, taskId, status);
            }
        }

        /// <summary>
        /// Retrieves Teams message threading information for a task from the SQLite cache.
        /// Returns (TeamId, ChannelId, RootMessageId, LastMessageId) or nulls if not found.
        /// </summary>
        private async Task<(string? TeamId, string? ChannelId, string? RootMessageId, string? LastMessageId)>
            GetTeamsMessageIdsAsync(long taskId, CancellationToken ct)
        {
            const string sql = @"
        SELECT TeamId, ChannelId, RootMessageId, LastMessageId
        FROM TaskIdMap
        WHERE TaskId = $taskId;";

            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$taskId", taskId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                var teamId = reader.IsDBNull(0) ? null : reader.GetString(0);
                var channelId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var rootMessageId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var lastMessageId = reader.IsDBNull(3) ? null : reader.GetString(3);

                return (teamId, channelId, rootMessageId, lastMessageId);
            }

            return (null, null, null, null);
        }

        private static string ExtractSqlitePathOrFallback(string? connectionString, string contentRootPath)
        {
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
            return Path.Combine(contentRootPath, "DB", "ConfluenceSyncServiceDB.db");
        }
    }
}
