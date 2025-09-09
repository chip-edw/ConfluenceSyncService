using Microsoft.Data.Sqlite;
namespace ConfluenceSyncService.Utilities;

public static class SqliteSchemaUpgrader
{
    // Adds columns to TaskIdMap if missing (idempotent, safe to call repeatedly)
    public static void EnsureChaserColumns(string dbPath, Serilog.ILogger log)
    {
        log.Information("SqliteSchemaUpgrader: Connecting to database at {DbPath}", dbPath);

        using var conn = new SqliteConnection($"Data Source={dbPath};");
        conn.Open();

        // Check if database file exists and log basic info
        var fileInfo = new FileInfo(dbPath);
        log.Information("Database file exists: {Exists}, Size: {Size} bytes", fileInfo.Exists, fileInfo.Length);

        // List all tables in the database
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        log.Information("Database contains tables: {Tables}", string.Join(", ", tables));

        // Handle corrupted indexes first, before any table operations
        try
        {
            EnsureChaserIndexes(conn, log);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 11)
        {
            log.Warning("Corrupted database schema detected, attempting repair...");
            RepairCorruptedIndexes(conn, log);
        }

        bool HasCol(string name)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info('TaskIdMap');";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    if (string.Equals(rdr.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 11)
            {
                log.Warning("Table info query failed due to corruption, assuming column {ColumnName} missing", name);
                return false;
            }
        }

        void AddCol(string ddl)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        var added = new List<string>();
        if (!HasCol("NextChaseAtUtcCached"))
        {
            AddCol("ALTER TABLE TaskIdMap ADD COLUMN NextChaseAtUtcCached TEXT NULL;");
            added.Add("NextChaseAtUtcCached");
        }
        if (!HasCol("LastChaseAtUtc"))
        {
            AddCol("ALTER TABLE TaskIdMap ADD COLUMN LastChaseAtUtc TEXT NULL;");
            added.Add("LastChaseAtUtc");
        }
        if (added.Count > 0)
            log.Information("SqliteSchemaUpgrader: Added columns to TaskIdMap: {Columns}", added);
    }

    private static void EnsureChaserIndexes(SqliteConnection conn, Serilog.ILogger log)
    {
        var requiredIndexes = new Dictionary<string, string>
        {
            ["IX_TaskIdMap_NextChaseAtUtcCached"] = "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_NextChaseAtUtcCached ON TaskIdMap(NextChaseAtUtcCached)",
            ["IX_TaskIdMap_AckExpiresUtc"] = "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_AckExpiresUtc ON TaskIdMap(AckExpiresUtc)",
            ["IX_TaskIdMap_CorrelationId"] = "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_CorrelationId ON TaskIdMap(CorrelationId)",
            ["IX_TaskIdMap_SpItemId"] = "CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskIdMap_SpItemId ON TaskIdMap(SpItemId)",
            ["IX_TaskIdMap_TeamId_ChannelId"] = "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_TeamId_ChannelId ON TaskIdMap(TeamId, ChannelId)",
            ["IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId"] = "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId ON TaskIdMap(CustomerId, PhaseName, TaskName, WorkflowId)"
        };

        bool IndexExists(string indexName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name=@indexName";
            cmd.Parameters.AddWithValue("@indexName", indexName);
            return cmd.ExecuteScalar() != null;
        }

        var created = new List<string>();
        foreach (var (indexName, createSql) in requiredIndexes)
        {
            if (!IndexExists(indexName))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = createSql;
                cmd.ExecuteNonQuery();
                created.Add(indexName);
            }
        }

        if (created.Count > 0)
            log.Information("SqliteSchemaUpgrader: Created indexes on TaskIdMap: {Indexes}", created);
    }

    private static void RepairCorruptedIndexes(SqliteConnection conn, Serilog.ILogger log)
    {
        log.Information("Repairing corrupted TaskIdMap indexes...");

        var indexesToDrop = new[]
        {
            "IX_TaskIdMap_NextChaseAtUtcCached",
            "IX_TaskIdMap_AckExpiresUtc",
            "IX_TaskIdMap_CorrelationId",
            "IX_TaskIdMap_SpItemId",
            "IX_TaskIdMap_TeamId_ChannelId",
            "IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId"
        };

        // Drop all potentially corrupted indexes
        foreach (var indexName in indexesToDrop)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP INDEX IF EXISTS {indexName}";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                log.Warning("Failed to drop index {IndexName}: {Error}", indexName, ex.Message);
            }
        }

        // Recreate all indexes fresh
        var recreateCommands = new[]
        {
            "CREATE INDEX IX_TaskIdMap_NextChaseAtUtcCached ON TaskIdMap(NextChaseAtUtcCached)",
            "CREATE INDEX IX_TaskIdMap_AckExpiresUtc ON TaskIdMap(AckExpiresUtc)",
            "CREATE INDEX IX_TaskIdMap_CorrelationId ON TaskIdMap(CorrelationId)",
            "CREATE UNIQUE INDEX IX_TaskIdMap_SpItemId ON TaskIdMap(SpItemId)",
            "CREATE INDEX IX_TaskIdMap_TeamId_ChannelId ON TaskIdMap(TeamId, ChannelId)",
            "CREATE INDEX IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId ON TaskIdMap(CustomerId, PhaseName, TaskName, WorkflowId)"
        };

        var repaired = new List<string>();
        foreach (var createSql in recreateCommands)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = createSql;
                cmd.ExecuteNonQuery();
                repaired.Add(createSql.Split(' ')[2]); // Extract index name
            }
            catch (SqliteException ex)
            {
                log.Error("Failed to recreate index: {CreateSql}, Error: {Error}", createSql, ex.Message);
            }
        }

        log.Information("SqliteSchemaUpgrader: Repaired corrupted indexes: {Indexes}", repaired);
    }
}
