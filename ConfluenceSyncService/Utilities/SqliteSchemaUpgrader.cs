using Microsoft.Data.Sqlite;


namespace ConfluenceSyncService.Utilities;

public static class SqliteSchemaUpgrader
{
    // Adds columns to TaskIdMap if missing (idempotent, safe to call repeatedly)
    public static void EnsureChaserColumns(string dbPath, Serilog.ILogger log)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        conn.Open();

        bool HasCol(string name)
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
}

