using ConfluenceSyncService.Identity;
using ConfluenceSyncService.Options;
using ConfluenceSyncService.Security;
using ConfluenceSyncService.SharePoint;
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
        IConfiguration config,           // ← ADD THIS
        IHostEnvironment env)            // ← ADD THIS
    {
        private readonly string _dbPath = ExtractSqlitePathOrFallback(
            config.GetConnectionString("ConfluenceSync"),
            env.ContentRootPath);

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
                    // The 'id' parameter is the SpItemId, so we need to look up TaskId first
                    if (long.TryParse(id, out var spItemId))
                    {
                        try
                        {
                            await UpdateCacheStatusAsync(spItemId, Models.TaskStatus.Completed, ct);
                            log.LogInformation("ACK: Updated SQLite cache Status='Completed' for SpItemId={SpItemId}", spItemId);
                        }
                        catch (Exception cacheEx)
                        {
                            log.LogError(cacheEx, "ACK: Failed to update SQLite cache for SpItemId={SpItemId}. Cache now stale!", spItemId);
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
        /// Updates the SQLite cache Status field for a task identified by SpItemId.
        /// This keeps the cache in sync with SharePoint after ACK.
        /// </summary>
        private async Task UpdateCacheStatusAsync(long spItemId, string status, CancellationToken ct)
        {
            const string sql = @"
                UPDATE TaskIdMap
                SET Status = $status
                WHERE SpItemId = $spItemId;";

            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$spItemId", spItemId);

            var rows = await cmd.ExecuteNonQueryAsync(ct);

            if (rows == 0)
            {
                log.LogWarning("ACK cache update: 0 rows affected for SpItemId={SpItemId}. Task may not exist in cache.", spItemId);
            }
            else
            {
                log.LogInformation("ACK cache update: Updated {Rows} row(s) for SpItemId={SpItemId} to Status='{Status}'", rows, spItemId, status);
            }
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
