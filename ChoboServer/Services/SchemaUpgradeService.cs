using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Services;

public static class SchemaUpgradeService
{
    public static async Task UpgradeAsync(ChoboDbContext db, SchemaStateEntity schema)
    {
        if (schema.SchemaVersion > ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema version {schema.SchemaVersion} is newer than server-supported schema version {ChoboApi.SchemaVersion}.");
        }

        while (schema.SchemaVersion < ChoboApi.SchemaVersion)
        {
            switch (schema.SchemaVersion)
            {
                case 1:
                    await UpgradeFromV1ToV2Async(db);
                    schema.SchemaVersion = 2;
                    schema.AppliedMigrationId = "0002_serilog_sqlite_sink_logs";
                    schema.AppliedAt = DateTimeOffset.UtcNow;
                    break;
                case 2:
                    await UpgradeFromV2ToV3Async(db);
                    schema.SchemaVersion = 3;
                    schema.AppliedMigrationId = "0003_policy_source_cluster";
                    schema.AppliedAt = DateTimeOffset.UtcNow;
                    break;
                case 3:
                    await UpgradeFromV3ToV4Async(db);
                    schema.SchemaVersion = 4;
                    schema.AppliedMigrationId = "0004_policy_selector_json_version";
                    schema.AppliedAt = DateTimeOffset.UtcNow;
                    break;
                default:
                    throw new InvalidOperationException($"No upgrade path is registered from schema version {schema.SchemaVersion}.");
            }
        }

        schema.ProductVersion = ChoboApi.ServerVersion;
        await db.SaveChangesAsync();
    }

    private static async Task UpgradeFromV1ToV2Async(ChoboDbContext db)
    {
        if (!await ColumnExistsAsync(db, "ApplicationLogEntries", "Message"))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ApplicationLogEntries_v2 (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT,
                Level VARCHAR(10),
                Exception TEXT,
                RenderedMessage TEXT,
                Properties TEXT
            );

            INSERT INTO ApplicationLogEntries_v2 (Id, Timestamp, Level, Exception, RenderedMessage, Properties)
            SELECT Id, Timestamp, Level, Exception, Message, json_object('SourceContext', Category)
            FROM ApplicationLogEntries
            WHERE EXISTS (SELECT 1 FROM pragma_table_info('ApplicationLogEntries') WHERE name = 'Message');

            DROP TABLE ApplicationLogEntries;
            ALTER TABLE ApplicationLogEntries_v2 RENAME TO ApplicationLogEntries;
            """);
    }

    private static async Task<bool> ColumnExistsAsync(ChoboDbContext db, string tableName, string columnName)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }
    }

    private static async Task UpgradeFromV2ToV3Async(ChoboDbContext db)
    {
        if (await ColumnExistsAsync(db, "BackupPolicies", "SourceClusterId"))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE BackupPolicies ADD COLUMN SourceClusterId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_SourceClusterId ON BackupPolicies (SourceClusterId);
            """);
    }

    private static async Task UpgradeFromV3ToV4Async(ChoboDbContext db)
    {
        if (await ColumnExistsAsync(db, "BackupPolicies", "SelectorJsonVersion"))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE BackupPolicies ADD COLUMN SelectorJsonVersion INTEGER NOT NULL DEFAULT 1;
            """);
    }
}
