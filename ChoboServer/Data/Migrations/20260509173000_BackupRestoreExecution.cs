using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("20260509173000_BackupRestoreExecution")]
public sealed class BackupRestoreExecution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE ClickHouseClusters ADD COLUMN BackupRestoreMaxDop INTEGER NULL;
            ALTER TABLE BackupPolicies ADD COLUMN TargetId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

            CREATE TABLE IF NOT EXISTS SchemaDefinitions (
                Id TEXT NOT NULL CONSTRAINT PK_SchemaDefinitions PRIMARY KEY,
                SchemaHash TEXT NOT NULL,
                Database TEXT NOT NULL,
                "Table" TEXT NOT NULL,
                Engine TEXT NOT NULL,
                CreateTableSql TEXT NOT NULL,
                ColumnsJson TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Backups (
                Id TEXT NOT NULL CONSTRAINT PK_Backups PRIMARY KEY,
                TriggerType INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                BackupType INTEGER NOT NULL,
                SourceClusterId TEXT NOT NULL,
                TargetId TEXT NOT NULL,
                PolicyId TEXT NULL,
                ScheduleId TEXT NULL,
                ManualRequestJson TEXT NULL,
                RequestedByUserId TEXT NULL,
                RequestedByName TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                QueuedAt INTEGER NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_Backups_ClickHouseClusters_SourceClusterId FOREIGN KEY (SourceClusterId) REFERENCES ClickHouseClusters (Id) ON DELETE CASCADE,
                CONSTRAINT FK_Backups_BackupTargets_TargetId FOREIGN KEY (TargetId) REFERENCES BackupTargets (Id) ON DELETE CASCADE,
                CONSTRAINT FK_Backups_BackupPolicies_PolicyId FOREIGN KEY (PolicyId) REFERENCES BackupPolicies (Id),
                CONSTRAINT FK_Backups_BackupSchedules_ScheduleId FOREIGN KEY (ScheduleId) REFERENCES BackupSchedules (Id)
            );

            CREATE TABLE IF NOT EXISTS BackupTables (
                Id TEXT NOT NULL CONSTRAINT PK_BackupTables PRIMARY KEY,
                BackupId TEXT NOT NULL,
                Database TEXT NOT NULL,
                "Table" TEXT NOT NULL,
                Engine TEXT NOT NULL,
                DataBackedUp INTEGER NOT NULL,
                SchemaDefinitionId TEXT NOT NULL,
                S3Path TEXT NOT NULL,
                Status INTEGER NOT NULL,
                ClickHouseOperationId TEXT NULL,
                ClickHouseStatus TEXT NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_BackupTables_Backups_BackupId FOREIGN KEY (BackupId) REFERENCES Backups (Id) ON DELETE CASCADE,
                CONSTRAINT FK_BackupTables_SchemaDefinitions_SchemaDefinitionId FOREIGN KEY (SchemaDefinitionId) REFERENCES SchemaDefinitions (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Restores (
                Id TEXT NOT NULL CONSTRAINT PK_Restores PRIMARY KEY,
                BackupId TEXT NOT NULL,
                TargetClusterId TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Append INTEGER NOT NULL,
                AllowSchemaMismatch INTEGER NOT NULL,
                RequestJson TEXT NOT NULL,
                RequestedByUserId TEXT NULL,
                RequestedByName TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                QueuedAt INTEGER NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_Restores_Backups_BackupId FOREIGN KEY (BackupId) REFERENCES Backups (Id) ON DELETE CASCADE,
                CONSTRAINT FK_Restores_ClickHouseClusters_TargetClusterId FOREIGN KEY (TargetClusterId) REFERENCES ClickHouseClusters (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS RestoreTables (
                Id TEXT NOT NULL CONSTRAINT PK_RestoreTables PRIMARY KEY,
                RestoreId TEXT NOT NULL,
                BackupTableId TEXT NOT NULL,
                SourceDatabase TEXT NOT NULL,
                SourceTable TEXT NOT NULL,
                TargetDatabase TEXT NOT NULL,
                TargetTable TEXT NOT NULL,
                Status INTEGER NOT NULL,
                ClickHouseOperationId TEXT NULL,
                ClickHouseStatus TEXT NULL,
                Warning TEXT NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_RestoreTables_Restores_RestoreId FOREIGN KEY (RestoreId) REFERENCES Restores (Id) ON DELETE CASCADE,
                CONSTRAINT FK_RestoreTables_BackupTables_BackupTableId FOREIGN KEY (BackupTableId) REFERENCES BackupTables (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_TargetId ON BackupPolicies (TargetId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SchemaDefinitions_SchemaHash ON SchemaDefinitions (SchemaHash);
            CREATE INDEX IF NOT EXISTS IX_Backups_Status ON Backups (Status);
            CREATE INDEX IF NOT EXISTS IX_Backups_PolicyId ON Backups (PolicyId);
            CREATE INDEX IF NOT EXISTS IX_Backups_ScheduleId ON Backups (ScheduleId);
            CREATE INDEX IF NOT EXISTS IX_Backups_SourceClusterId ON Backups (SourceClusterId);
            CREATE INDEX IF NOT EXISTS IX_Backups_TargetId ON Backups (TargetId);
            CREATE INDEX IF NOT EXISTS IX_Backups_CreatedAt ON Backups (CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_BackupTables_BackupId ON BackupTables (BackupId);
            CREATE INDEX IF NOT EXISTS IX_BackupTables_Database_Table ON BackupTables (Database, "Table");
            CREATE INDEX IF NOT EXISTS IX_BackupTables_Status ON BackupTables (Status);
            CREATE INDEX IF NOT EXISTS IX_Restores_Status ON Restores (Status);
            CREATE INDEX IF NOT EXISTS IX_Restores_BackupId ON Restores (BackupId);
            CREATE INDEX IF NOT EXISTS IX_Restores_TargetClusterId ON Restores (TargetClusterId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTables_RestoreId ON RestoreTables (RestoreId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTables_BackupTableId ON RestoreTables (BackupTableId);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS RestoreTables;
            DROP TABLE IF EXISTS Restores;
            DROP TABLE IF EXISTS BackupTables;
            DROP TABLE IF EXISTS Backups;
            DROP TABLE IF EXISTS SchemaDefinitions;
            """);
    }
}
