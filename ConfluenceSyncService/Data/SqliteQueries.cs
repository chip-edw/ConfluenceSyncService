using Microsoft.Data.Sqlite;
using System.Globalization;

namespace ConfluenceSyncService.Data;

public static class SqliteQueries
{
    public sealed record DueCandidate(
        long TaskId,
        long SpItemId,
        string TaskName,
        string Region,
        string AnchorDateType,
        string TeamId,
        string ChannelId,
        string RootMessageId,
        int AckVersion,
        string CustomerId,           // NEW: For grouping by customer
        int? StartOffsetDays);       // NEW: For sequential group ordering

    public static async Task<List<DueCandidate>> GetDueChaserCandidatesAsync(string dbPath, int limit, Serilog.ILogger log, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
 SELECT TaskId, SpItemId, TaskName, Region, AnchorDateType, TeamId, ChannelId, RootMessageId, 
        IFNULL(AckVersion,0) as AckVersion, 
        IFNULL(CustomerId,'') as CustomerId,
        StartOffsetDays
 FROM TaskIdMap
 WHERE NextChaseAtUtcCached IS NOT NULL
   AND datetime(NextChaseAtUtcCached) <= datetime('now')
   AND (Status IS NULL OR Status != 'Completed')
 ORDER BY datetime(NextChaseAtUtcCached) ASC
 LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<DueCandidate>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new DueCandidate(
                rdr.GetInt64(0),                                        // TaskId
                rdr.GetInt64(1),                                        // SpItemId
                rdr.IsDBNull(2) ? "" : rdr.GetString(2),               // TaskName
                rdr.IsDBNull(3) ? "" : rdr.GetString(3),               // Region
                rdr.IsDBNull(4) ? "" : rdr.GetString(4),               // AnchorDateType
                rdr.IsDBNull(5) ? "" : rdr.GetString(5),               // TeamId
                rdr.IsDBNull(6) ? "" : rdr.GetString(6),               // ChannelId
                rdr.IsDBNull(7) ? "" : rdr.GetString(7),               // RootMessageId
                rdr.GetInt32(8),                                        // AckVersion
                rdr.IsDBNull(9) ? "" : rdr.GetString(9),               // CustomerId
                rdr.IsDBNull(10) ? null : rdr.GetInt32(10)             // StartOffsetDays
            ));
        }
        return list;
    }

    /// <summary>
    /// Gets all tasks for a specific customer and anchor date type to check group completion status.
    /// Used by sequential dependency filtering.
    /// </summary>
    public static async Task<List<GroupTaskStatus>> GetGroupTaskStatusAsync(
        string dbPath,
        string customerId,
        string anchorDateType,
        int startOffsetDays,
        Serilog.ILogger log,
        CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
 SELECT TaskId, TaskName, IFNULL(Status, 'Not Started') as Status, StartOffsetDays
 FROM TaskIdMap
 WHERE CustomerId = $customerId
   AND AnchorDateType = $anchorDateType
   AND StartOffsetDays = $startOffsetDays
   AND State = 'linked'
 ORDER BY TaskName;";

        cmd.Parameters.AddWithValue("$customerId", customerId);
        cmd.Parameters.AddWithValue("$anchorDateType", anchorDateType);
        cmd.Parameters.AddWithValue("$startOffsetDays", startOffsetDays);

        var list = new List<GroupTaskStatus>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new GroupTaskStatus(
                rdr.GetInt64(0),                    // TaskId
                rdr.GetString(1),                   // TaskName
                rdr.GetString(2),                   // Status
                rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3) // StartOffsetDays
            ));
        }
        return list;
    }

    public sealed record GroupTaskStatus(long TaskId, string TaskName, string Status, int StartOffsetDays);

    public static async Task UpdateTaskStatusAsync(string dbPath, long taskId, string status, Serilog.ILogger log, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
 UPDATE TaskIdMap
 SET Status = $status
 WHERE TaskId = $taskId;";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$taskId", taskId);
        await cmd.ExecuteNonQueryAsync(ct);
        log.Information("StatusCacheUpdate taskId={TaskId} status={Status}", taskId, status);
    }

    public static async Task UpdateNextChaseCachedAsync(string dbPath, long taskId, DateTimeOffset nextUtc, Serilog.ILogger log, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
 UPDATE TaskIdMap
 SET NextChaseAtUtcCached = $nextUtc
 WHERE TaskId = $taskId;";
        cmd.Parameters.AddWithValue("$nextUtc", nextUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$taskId", taskId);
        await cmd.ExecuteNonQueryAsync(ct);
        log.Information("ChaserScheduleMirrorWrite taskId={TaskId} nextChaseAtUtcCached={Next}", taskId, nextUtc);
    }

    public static async Task UpdateChaserMirrorAsync(string dbPath, long taskId, int newAckVersion, DateTimeOffset ackExpiresUtc, DateTimeOffset nextChaseUtc, Serilog.ILogger log, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
 UPDATE TaskIdMap
 SET AckVersion = $ver,
     AckExpiresUtc = $exp,
     LastChaseAtUtc = $last,
     NextChaseAtUtcCached = $next
 WHERE TaskId = $taskId;";
        cmd.Parameters.AddWithValue("$ver", newAckVersion);
        cmd.Parameters.AddWithValue("$exp", ackExpiresUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$last", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$next", nextChaseUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$taskId", taskId);
        await cmd.ExecuteNonQueryAsync(ct);
        log.Information("SqliteUpdateSuccess taskId={TaskId} ackVersion={Ver} ackExpiresUtc={Exp} nextChaseAtUtcCached={Next}",
        taskId, newAckVersion, ackExpiresUtc, nextChaseUtc);
    }

    /// <summary>
    /// Updates the StartOffsetDays field for a task. Used during sync operations.
    /// </summary>
    public static async Task UpdateStartOffsetDaysAsync(string dbPath, long taskId, int startOffsetDays, Serilog.ILogger log, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
 UPDATE TaskIdMap
 SET StartOffsetDays = $startOffsetDays
 WHERE TaskId = $taskId;";
        cmd.Parameters.AddWithValue("$startOffsetDays", startOffsetDays);
        cmd.Parameters.AddWithValue("$taskId", taskId);
        await cmd.ExecuteNonQueryAsync(ct);
        log.Information("StartOffsetDaysUpdate taskId={TaskId} startOffsetDays={StartOffsetDays}", taskId, startOffsetDays);
    }
}
