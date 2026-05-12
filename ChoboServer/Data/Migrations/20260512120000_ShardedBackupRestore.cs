using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("20260512120000_ShardedBackupRestore")]
public sealed class ShardedBackupRestore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE ClickHouseClusters ADD COLUMN ClickHouseClusterName TEXT NULL;
            ALTER TABLE Restores ADD COLUMN Layout INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE Restores ADD COLUMN SourceShard INTEGER NULL;
            ALTER TABLE Restores ADD COLUMN TargetShard INTEGER NULL;

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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS RestoreTableShards;
            DROP TABLE IF EXISTS BackupTableShards;
            """);
    }
}
