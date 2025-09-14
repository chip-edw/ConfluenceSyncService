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

        bool HasCol(string name, string tableName = "TaskIdMap")
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info('{tableName}');";
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
                log.Warning("Table info query failed due to corruption, assuming column {ColumnName} missing in {TableName}", name, tableName);
                return false;
            }
        }

        void AddCol(string ddl)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        // ADD COLUMNS FIRST
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
        if (!HasCol("Status"))
        {
            AddCol("ALTER TABLE TaskIdMap ADD COLUMN Status TEXT NULL;");
            added.Add("Status");
        }
        if (added.Count > 0)
            log.Information("SqliteSchemaUpgrader: Added columns to TaskIdMap: {Columns}", added);

        // Also ensure TableSyncStates has SyncTracker column
        if (!HasCol("TableSyncStates", "SyncTracker"))
        {
            AddCol("ALTER TABLE TableSyncStates ADD COLUMN SyncTracker TEXT DEFAULT 'No';");
            added.Add("TableSyncStates.SyncTracker");
        }

        if (added.Count > 0)
            log.Information("SqliteSchemaUpgrader: Added columns: {Columns}", added);

        // HANDLE INDEXES AFTER COLUMNS ARE ADDED
        try
        {
            EnsureChaserIndexes(conn, log);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 11)
        {
            log.Warning("Corrupted database schema detected, attempting repair...");
            RepairCorruptedIndexes(conn, log);
        }

        // Force all changes to main database file
        using var checkpointCmd = conn.CreateCommand();
        checkpointCmd.CommandText = "PRAGMA wal_checkpoint(FULL)";
        checkpointCmd.ExecuteNonQuery();
        log.Information("Database changes checkpointed to main file");
    }

    public static void EnsureSyncTrackerColumn(string dbPath, Serilog.ILogger logger)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // Check if SyncTracker column exists
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(TableSyncStates)";
        using var reader = checkCmd.ExecuteReader();

        bool hasSyncTracker = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "SyncTracker") // Column name is in index 1
            {
                hasSyncTracker = true;
                break;
            }
        }
        reader.Close();

        if (!hasSyncTracker)
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE TableSyncStates ADD COLUMN SyncTracker TEXT DEFAULT 'Yes'";
            alterCmd.ExecuteNonQuery();
            logger.Information("Added SyncTracker column to TableSyncStates with default 'Yes'");
        }
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
            ["IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId"] = "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId ON TaskIdMap(CustomerId, PhaseName, TaskName, WorkflowId)",
            ["IX_TaskIdMap_Status"] = "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_Status ON TaskIdMap(Status)",

            ["IX_TableSyncStates_SyncTracker"] = "CREATE INDEX IF NOT EXISTS IX_TableSyncStates_SyncTracker ON TableSyncStates(SyncTracker)"
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
            "IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId",
            "IX_TaskIdMap_Status"
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
            "CREATE INDEX IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId ON TaskIdMap(CustomerId, PhaseName, TaskName, WorkflowId)",
            "CREATE INDEX IX_TaskIdMap_Status ON TaskIdMap(Status)"
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

        // Checkpoint immediately after repair operations
        using var checkpointCmd = conn.CreateCommand();
        checkpointCmd.CommandText = "PRAGMA wal_checkpoint(FULL)";
        checkpointCmd.ExecuteNonQuery();
        log.Information("Database repair changes checkpointed to main file");
    }
}
