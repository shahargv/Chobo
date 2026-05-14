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
                case 4:
                    await UpgradeFromV4ToV5Async(db);
                    schema.SchemaVersion = 5;
                    schema.AppliedMigrationId = "0005_schedule_missed_run_grace_period";
                    schema.AppliedAt = DateTimeOffset.UtcNow;
                    break;
                case 5:
                    await UpgradeFromV5ToV6Async(db);
                    schema.SchemaVersion = 6;
                    schema.AppliedMigrationId = "0006_sharded_backup_restore";
                    schema.AppliedAt = DateTimeOffset.UtcNow;
                    break;
                case 6:
                    await UpgradeFromV6ToV7Async(db);
                    schema.SchemaVersion = 7;
                    schema.AppliedMigrationId = "0007_backup_restore_failure_reason";
                    schema.AppliedAt = DateTimeOffset.UtcNow;
                    break;
                case 7:
                    await UpgradeFromV7ToV8Async(db);
                    schema.SchemaVersion = 8;
                    schema.AppliedMigrationId = "0008_backup_retention_cleanup";
                    schema.AppliedAt = DateTimeOffset.UtcNow;
                    break;
                default:
                    throw new InvalidOperationException($"No upgrade path is registered from schema version {schema.SchemaVersion}.");
            }
        }

        schema.ProductVersion = ChoboApi.ProductVersion;
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
                Timestamp INTEGER,
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

    private static async Task UpgradeFromV4ToV5Async(ChoboDbContext db)
    {
        if (await ColumnExistsAsync(db, "BackupSchedules", "MissedRunGracePeriod"))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE BackupSchedules ADD COLUMN MissedRunGracePeriod TEXT NULL;
            """);
    }

    private static async Task UpgradeFromV5ToV6Async(ChoboDbContext db)
    {
        if (!await ColumnExistsAsync(db, "ClickHouseClusters", "ClickHouseClusterName"))
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE ClickHouseClusters ADD COLUMN ClickHouseClusterName TEXT NULL;
                """);
        }

        if (!await ColumnExistsAsync(db, "Restores", "Layout"))
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE Restores ADD COLUMN Layout INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE Restores ADD COLUMN SourceShard INTEGER NULL;
                ALTER TABLE Restores ADD COLUMN TargetShard INTEGER NULL;
                """);
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS BackupTableShards (
                Id TEXT NOT NULL CONSTRAINT PK_BackupTableShards PRIMARY KEY,
                BackupTableId TEXT NOT NULL,
                SourceShardNumber INTEGER NOT NULL,
                SourceShardName TEXT NULL,
                ReplicaNumber INTEGER NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL,
                UseTls INTEGER NOT NULL,
                S3Path TEXT NOT NULL,
                Status INTEGER NOT NULL,
                ClickHouseOperationId TEXT NULL,
                ClickHouseStatus TEXT NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_BackupTableShards_BackupTables_BackupTableId FOREIGN KEY (BackupTableId) REFERENCES BackupTables (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS RestoreTableShards (
                Id TEXT NOT NULL CONSTRAINT PK_RestoreTableShards PRIMARY KEY,
                RestoreTableId TEXT NOT NULL,
                BackupTableShardId TEXT NOT NULL,
                SourceShardNumber INTEGER NOT NULL,
                TargetShardNumber INTEGER NULL,
                TargetShardName TEXT NULL,
                TargetReplicaNumber INTEGER NULL,
                TargetHost TEXT NOT NULL,
                TargetPort INTEGER NOT NULL,
                TargetUseTls INTEGER NOT NULL,
                LayoutRole TEXT NOT NULL,
                RestoreDatabase TEXT NOT NULL,
                RestoreTableName TEXT NOT NULL,
                Status INTEGER NOT NULL,
                ClickHouseOperationId TEXT NULL,
                ClickHouseStatus TEXT NULL,
                Warning TEXT NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_RestoreTableShards_RestoreTables_RestoreTableId FOREIGN KEY (RestoreTableId) REFERENCES RestoreTables (Id) ON DELETE CASCADE,
                CONSTRAINT FK_RestoreTableShards_BackupTableShards_BackupTableShardId FOREIGN KEY (BackupTableShardId) REFERENCES BackupTableShards (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupTableId ON BackupTableShards (BackupTableId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupTableId_SourceShardNumber ON BackupTableShards (BackupTableId, SourceShardNumber);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_Status ON BackupTableShards (Status);
            CREATE INDEX IF NOT EXISTS IX_RestoreTableShards_RestoreTableId ON RestoreTableShards (RestoreTableId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTableShards_BackupTableShardId ON RestoreTableShards (BackupTableShardId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTableShards_Status ON RestoreTableShards (Status);
            """);
    }

    private static async Task UpgradeFromV6ToV7Async(ChoboDbContext db)
    {
        if (!await ColumnExistsAsync(db, "Backups", "FailureReason"))
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE Backups ADD COLUMN FailureReason TEXT NULL;
                """);
        }

        if (!await ColumnExistsAsync(db, "Restores", "FailureReason"))
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE Restores ADD COLUMN FailureReason TEXT NULL;
                """);
        }
    }

    private static async Task UpgradeFromV7ToV8Async(ChoboDbContext db)
    {
        if (!await ColumnExistsAsync(db, "BackupPolicies", "RetentionMinutes"))
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE BackupPolicies ADD COLUMN RetentionMinutes INTEGER NULL;
                ALTER TABLE BackupPolicies ADD COLUMN MinBackupsToKeep INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE BackupPolicies ADD COLUMN FailedBackupRetentionMode INTEGER NOT NULL DEFAULT 0;
                """);
        }

        if (!await ColumnExistsAsync(db, "Backups", "IsPinned"))
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE Backups ADD COLUMN IsPinned INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE Backups ADD COLUMN PinnedAt INTEGER NULL;
                ALTER TABLE Backups ADD COLUMN PinnedByUserId TEXT NULL;
                ALTER TABLE Backups ADD COLUMN PinnedByName TEXT NULL;
                ALTER TABLE Backups ADD COLUMN DeletionReason TEXT NULL;
                ALTER TABLE Backups ADD COLUMN DeletionRequestedAt INTEGER NULL;
                ALTER TABLE Backups ADD COLUMN DeletionStartedAt INTEGER NULL;
                ALTER TABLE Backups ADD COLUMN DeletedAt INTEGER NULL;
                ALTER TABLE Backups ADD COLUMN DeletionError TEXT NULL;
                ALTER TABLE Backups ADD COLUMN DeletionAttemptCount INTEGER NOT NULL DEFAULT 0;
                """);
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_Backups_IsPinned ON Backups (IsPinned);
            CREATE INDEX IF NOT EXISTS IX_Backups_DeletionRequestedAt ON Backups (DeletionRequestedAt);
            """);
    }
}
