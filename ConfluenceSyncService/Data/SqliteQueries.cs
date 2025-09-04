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
        int AckVersion);

    public static async Task<List<DueCandidate>> GetDueChaserCandidatesAsync(string dbPath, int limit, Serilog.ILogger log, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
 SELECT TaskId, SpItemId, TaskName, Region, AnchorDateType, TeamId, ChannelId, RootMessageId, IFNULL(AckVersion,0) as AckVersion
 FROM TaskIdMap
 WHERE NextChaseAtUtcCached IS NOT NULL
   AND datetime(NextChaseAtUtcCached) <= datetime('now')
 ORDER BY datetime(NextChaseAtUtcCached) ASC
 LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<DueCandidate>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new DueCandidate(
                rdr.GetInt64(0),
                rdr.GetInt64(1),
                rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                rdr.GetInt32(8)
            ));
        }
        return list;
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
}
