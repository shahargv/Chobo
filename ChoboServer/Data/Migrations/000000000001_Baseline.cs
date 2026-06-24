using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("000000000001_Baseline")]
public sealed class Baseline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS SchemaStates (
                Id INTEGER NOT NULL CONSTRAINT PK_SchemaStates PRIMARY KEY AUTOINCREMENT,
                SchemaVersion INTEGER NOT NULL,
                AppliedMigrationId TEXT NOT NULL,
                AppliedAt INTEGER NOT NULL,
                ProductVersion TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Users (
                Id TEXT NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
                UserName TEXT NOT NULL,
                IsActive INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                DeactivatedAt INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS AccessTokens (
                Id TEXT NOT NULL CONSTRAINT PK_AccessTokens PRIMARY KEY,
                UserId TEXT NOT NULL,
                Name TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                TokenLookupHash TEXT NOT NULL,
                Salt TEXT NOT NULL,
                IsActive INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                DeactivatedAt INTEGER NULL,
                CONSTRAINT FK_AccessTokens_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS AuditEntries (
                Id INTEGER NOT NULL CONSTRAINT PK_AuditEntries PRIMARY KEY AUTOINCREMENT,
                Timestamp INTEGER NOT NULL,
                ActorUserId TEXT NULL,
                ActorName TEXT NOT NULL,
                Action TEXT NOT NULL,
                EntityType TEXT NOT NULL,
                EntityId TEXT NULL,
                OperationId TEXT NULL,
                Details TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ApplicationLogEntries (
                Id INTEGER NOT NULL CONSTRAINT PK_ApplicationLogEntries PRIMARY KEY AUTOINCREMENT,
                Timestamp INTEGER,
                Level VARCHAR(10),
                Exception TEXT,
                RenderedMessage TEXT,
                OperationId TEXT NULL,
                Properties TEXT
            );

            CREATE TABLE IF NOT EXISTS SqliteSelfBackupStates (
                Id INTEGER NOT NULL CONSTRAINT PK_SqliteSelfBackupStates PRIMARY KEY AUTOINCREMENT,
                LastBackupAt INTEGER NULL,
                LastBackupPath TEXT NULL,
                LastAttemptAt INTEGER NULL,
                LastError TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS ClickHouseClusters (
                Id TEXT NOT NULL CONSTRAINT PK_ClickHouseClusters PRIMARY KEY,
                Name TEXT NOT NULL,
                Mode INTEGER NOT NULL,
                EncryptedUserName TEXT NULL,
                EncryptedUserNameKeyId TEXT NULL,
                EncryptedPassword TEXT NULL,
                EncryptedPasswordKeyId TEXT NULL,
                BackupRestoreMaxDop INTEGER NOT NULL,
                NodeMaxDopDefault INTEGER NOT NULL DEFAULT 1,
                NodeMaxDopOverridesJson TEXT NOT NULL DEFAULT '[]',
                ShardMaxDopDefault INTEGER NOT NULL DEFAULT 1,
                ShardMaxDopOverridesJson TEXT NOT NULL DEFAULT '[]',
                ClickHouseClusterName TEXT NULL,
                IsDeleted INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NULL,
                DeletedAt INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS ClickHouseAccessNodes (
                Id TEXT NOT NULL CONSTRAINT PK_ClickHouseAccessNodes PRIMARY KEY,
                ClusterId TEXT NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL,
                UseTls INTEGER NOT NULL,
                CONSTRAINT FK_ClickHouseAccessNodes_ClickHouseClusters_ClusterId FOREIGN KEY (ClusterId) REFERENCES ClickHouseClusters (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS BackupTargets (
                Id TEXT NOT NULL CONSTRAINT PK_BackupTargets PRIMARY KEY,
                Name TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Endpoint TEXT NOT NULL,
                Region TEXT NOT NULL,
                Bucket TEXT NOT NULL,
                PathPrefix TEXT NULL,
                ForcePathStyle INTEGER NOT NULL,
                EncryptedAccessKey TEXT NULL,
                EncryptedAccessKeyKeyId TEXT NULL,
                EncryptedSecretKey TEXT NULL,
                EncryptedSecretKeyKeyId TEXT NULL,
                IsDeleted INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NULL,
                DeletedAt INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS BackupPolicies (
                Id TEXT NOT NULL CONSTRAINT PK_BackupPolicies PRIMARY KEY,
                Name TEXT NOT NULL,
                SourceClusterId TEXT NOT NULL,
                TargetId TEXT NULL,
                ContentMode INTEGER NOT NULL DEFAULT 0,
                SelectorJsonVersion INTEGER NOT NULL,
                SelectorJson TEXT NOT NULL,
                FullRetentionMinutes INTEGER NULL,
                IncrementalRetentionMinutes INTEGER NULL,
                MinBackupsToKeep INTEGER NOT NULL,
                MinFullBackupsToKeep INTEGER NOT NULL,
                FailedBackupRetentionMode INTEGER NOT NULL,
                IsSystemDefault INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NULL,
                DeletedAt INTEGER NULL,
                CONSTRAINT FK_BackupPolicies_ClickHouseClusters_SourceClusterId FOREIGN KEY (SourceClusterId) REFERENCES ClickHouseClusters (Id) ON DELETE CASCADE,
                CONSTRAINT FK_BackupPolicies_BackupTargets_TargetId FOREIGN KEY (TargetId) REFERENCES BackupTargets (Id) ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS BackupSchedules (
                Id TEXT NOT NULL CONSTRAINT PK_BackupSchedules PRIMARY KEY,
                Name TEXT NOT NULL,
                PolicyId TEXT NOT NULL,
                BackupType INTEGER NOT NULL,
                CronExpression TEXT NOT NULL,
                TimeZoneId TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                MissedRunGracePeriod TEXT NULL,
                Description TEXT NULL,
                IsSystemDefault INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NULL,
                DeletedAt INTEGER NULL,
                CONSTRAINT FK_BackupSchedules_BackupPolicies_PolicyId FOREIGN KEY (PolicyId) REFERENCES BackupPolicies (Id) ON DELETE CASCADE
            );

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
                ContentMode INTEGER NOT NULL DEFAULT 0,
                SourceClusterId TEXT NOT NULL,
                TargetId TEXT NULL,
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
                FailureReason TEXT NULL,
                IsPinned INTEGER NOT NULL,
                PinnedAt INTEGER NULL,
                PinnedByUserId TEXT NULL,
                PinnedByName TEXT NULL,
                DeletionReason TEXT NULL,
                DeletionRequestedAt INTEGER NULL,
                DeletionStartedAt INTEGER NULL,
                DeletedAt INTEGER NULL,
                DeletionError TEXT NULL,
                DeletionAttemptCount INTEGER NOT NULL,
                CONSTRAINT FK_Backups_ClickHouseClusters_SourceClusterId FOREIGN KEY (SourceClusterId) REFERENCES ClickHouseClusters (Id) ON DELETE CASCADE,
                CONSTRAINT FK_Backups_BackupTargets_TargetId FOREIGN KEY (TargetId) REFERENCES BackupTargets (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_Backups_BackupPolicies_PolicyId FOREIGN KEY (PolicyId) REFERENCES BackupPolicies (Id),
                CONSTRAINT FK_Backups_BackupSchedules_ScheduleId FOREIGN KEY (ScheduleId) REFERENCES BackupSchedules (Id)
            );

            CREATE TABLE IF NOT EXISTS BackupTables (
                Id TEXT NOT NULL CONSTRAINT PK_BackupTables PRIMARY KEY,
                BackupId TEXT NOT NULL,
                EffectiveBackupType INTEGER NOT NULL,
                ParentFullBackupId TEXT NULL,
                ParentFullBackupTableId TEXT NULL,
                Database TEXT NOT NULL,
                "Table" TEXT NOT NULL,
                Engine TEXT NOT NULL,
                DataBackedUp INTEGER NOT NULL,
                SchemaDefinitionId TEXT NULL,
                S3Path TEXT NOT NULL,
                BackupSizeBytes INTEGER NULL,
                Status INTEGER NOT NULL,
                ClickHouseOperationId TEXT NULL,
                ClickHouseStatus TEXT NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_BackupTables_Backups_BackupId FOREIGN KEY (BackupId) REFERENCES Backups (Id) ON DELETE CASCADE,
                CONSTRAINT FK_BackupTables_SchemaDefinitions_SchemaDefinitionId FOREIGN KEY (SchemaDefinitionId) REFERENCES SchemaDefinitions (Id) ON DELETE SET NULL,
                CONSTRAINT FK_BackupTables_BackupTables_ParentFullBackupTableId FOREIGN KEY (ParentFullBackupTableId) REFERENCES BackupTables (Id)
            );

            CREATE TABLE IF NOT EXISTS BackupTableShards (
                Id TEXT NOT NULL CONSTRAINT PK_BackupTableShards PRIMARY KEY,
                BackupTableId TEXT NOT NULL,
                EffectiveBackupType INTEGER NOT NULL,
                ParentFullBackupId TEXT NULL,
                ParentFullBackupTableShardId TEXT NULL,
                SourceShardNumber INTEGER NOT NULL,
                SourceShardName TEXT NULL,
                ReplicaNumber INTEGER NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL,
                UseTls INTEGER NOT NULL,
                S3Path TEXT NOT NULL,
                BackupSizeBytes INTEGER NULL,
                Status INTEGER NOT NULL,
                ClickHouseOperationId TEXT NULL,
                ClickHouseStatus TEXT NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                CONSTRAINT FK_BackupTableShards_BackupTables_BackupTableId FOREIGN KEY (BackupTableId) REFERENCES BackupTables (Id) ON DELETE CASCADE,
                CONSTRAINT FK_BackupTableShards_BackupTableShards_ParentFullBackupTableShardId FOREIGN KEY (ParentFullBackupTableShardId) REFERENCES BackupTableShards (Id)
            );


            CREATE TABLE IF NOT EXISTS BackupRestoreQueueItems (
                Id TEXT NOT NULL CONSTRAINT PK_BackupRestoreQueueItems PRIMARY KEY,
                Kind INTEGER NOT NULL,
                Position INTEGER NOT NULL,
                IsForced INTEGER NOT NULL,
                ForcedAt INTEGER NULL,
                ForcedByUserId TEXT NULL,
                ForcedByName TEXT NULL,
                OperationId TEXT NOT NULL,
                TableId TEXT NOT NULL,
                ShardId TEXT NOT NULL,
                ClusterId TEXT NOT NULL,
                LogicalShardNumber INTEGER NOT NULL,
                LogicalShardName TEXT NULL,
                NodeHost TEXT NULL,
                NodePort INTEGER NULL,
                NodeUseTls INTEGER NULL,
                CreatedAt INTEGER NOT NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS Restores (
                Id TEXT NOT NULL CONSTRAINT PK_Restores PRIMARY KEY,
                BackupId TEXT NOT NULL,
                TargetClusterId TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Append INTEGER NOT NULL,
                AllowSchemaMismatch INTEGER NOT NULL,
                Layout INTEGER NOT NULL,
                SourceShard INTEGER NULL,
                TargetShard INTEGER NULL,
                RequestJson TEXT NOT NULL,
                RequestedByUserId TEXT NULL,
                RequestedByName TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                QueuedAt INTEGER NULL,
                StartedAt INTEGER NULL,
                CompletedAt INTEGER NULL,
                Error TEXT NULL,
                FailureReason TEXT NULL,
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
                Append INTEGER NOT NULL,
                AllowSchemaMismatch INTEGER NOT NULL,
                SchemaOnly INTEGER NOT NULL,
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
            """);

        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_UserName ON Users (UserName);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AccessTokens_TokenHash ON AccessTokens (TokenHash);
            CREATE INDEX IF NOT EXISTS IX_AccessTokens_TokenLookupHash ON AccessTokens (TokenLookupHash);
            CREATE INDEX IF NOT EXISTS IX_AccessTokens_IsActive_TokenLookupHash ON AccessTokens (IsActive, TokenLookupHash);
            CREATE INDEX IF NOT EXISTS IX_AccessTokens_UserId_IsActive ON AccessTokens (UserId, IsActive);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_Timestamp ON AuditEntries (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_ActorUserId_Timestamp ON AuditEntries (ActorUserId, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_EntityType_Timestamp ON AuditEntries (EntityType, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_OperationId_Timestamp ON AuditEntries (OperationId, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_Timestamp_Id ON AuditEntries (Timestamp, Id);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_OperationId_Timestamp_Id ON AuditEntries (OperationId, Timestamp, Id);
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Timestamp ON ApplicationLogEntries (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Level_Timestamp ON ApplicationLogEntries (Level, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_OperationId_Timestamp ON ApplicationLogEntries (OperationId, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Timestamp_Id ON ApplicationLogEntries (Timestamp, Id);
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_OperationId_Timestamp_Id ON ApplicationLogEntries (OperationId, Timestamp, Id);
            CREATE INDEX IF NOT EXISTS IX_ClickHouseClusters_IsDeleted_Name ON ClickHouseClusters (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_ClickHouseAccessNodes_ClusterId ON ClickHouseAccessNodes (ClusterId);
            CREATE INDEX IF NOT EXISTS IX_BackupTargets_IsDeleted_Name ON BackupTargets (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_IsDeleted_Name ON BackupPolicies (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_SourceClusterId ON BackupPolicies (SourceClusterId);
            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_TargetId ON BackupPolicies (TargetId);
            CREATE INDEX IF NOT EXISTS IX_BackupSchedules_IsDeleted_Name ON BackupSchedules (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupSchedules_PolicyId_IsDeleted ON BackupSchedules (PolicyId, IsDeleted);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SchemaDefinitions_SchemaHash ON SchemaDefinitions (SchemaHash);
            CREATE INDEX IF NOT EXISTS IX_Backups_Status ON Backups (Status);
            CREATE INDEX IF NOT EXISTS IX_Backups_PolicyId ON Backups (PolicyId);
            CREATE INDEX IF NOT EXISTS IX_Backups_ScheduleId ON Backups (ScheduleId);
            CREATE INDEX IF NOT EXISTS IX_Backups_SourceClusterId ON Backups (SourceClusterId);
            CREATE INDEX IF NOT EXISTS IX_Backups_TargetId ON Backups (TargetId);
            CREATE INDEX IF NOT EXISTS IX_Backups_CreatedAt ON Backups (CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Backups_PolicyId_BackupType_Status_CompletedAt ON Backups (PolicyId, BackupType, Status, CompletedAt);
            CREATE INDEX IF NOT EXISTS IX_Backups_PolicyId_Status_CompletedAt ON Backups (PolicyId, Status, CompletedAt);
            CREATE INDEX IF NOT EXISTS IX_Backups_ScheduleId_CreatedAt ON Backups (ScheduleId, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Backups_ScheduleId_Status_CompletedAt ON Backups (ScheduleId, Status, CompletedAt);
            CREATE INDEX IF NOT EXISTS IX_Backups_IsPinned ON Backups (IsPinned);
            CREATE INDEX IF NOT EXISTS IX_Backups_DeletionRequestedAt ON Backups (DeletionRequestedAt);
            CREATE INDEX IF NOT EXISTS IX_BackupTables_BackupId ON BackupTables (BackupId);
            CREATE INDEX IF NOT EXISTS IX_BackupTables_Database_Table ON BackupTables (Database, "Table");
            CREATE INDEX IF NOT EXISTS IX_BackupTables_Table ON BackupTables ("Table");
            CREATE INDEX IF NOT EXISTS IX_BackupTables_EffectiveBackupType_ParentFullBackupTableId ON BackupTables (EffectiveBackupType, ParentFullBackupTableId);
            CREATE INDEX IF NOT EXISTS IX_BackupTables_EffectiveBackupType_Database_Table ON BackupTables (EffectiveBackupType, Database, "Table");
            CREATE INDEX IF NOT EXISTS IX_BackupTables_Status ON BackupTables (Status);
            CREATE INDEX IF NOT EXISTS IX_BackupTables_ParentFullBackupTableId ON BackupTables (ParentFullBackupTableId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupTableId ON BackupTableShards (BackupTableId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupTableId_SourceShardNumber ON BackupTableShards (BackupTableId, SourceShardNumber);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_EffectiveBackupType_ParentFullBackupTableShardId ON BackupTableShards (EffectiveBackupType, ParentFullBackupTableShardId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_Status ON BackupTableShards (Status);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupTableShardId ON BackupTableShards (ParentFullBackupTableShardId);
            CREATE INDEX IF NOT EXISTS IX_BackupRestoreQueueItems_IsForced_Position ON BackupRestoreQueueItems (IsForced, Position);
            CREATE INDEX IF NOT EXISTS IX_BackupRestoreQueueItems_Kind_OperationId ON BackupRestoreQueueItems (Kind, OperationId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupRestoreQueueItems_ShardId ON BackupRestoreQueueItems (ShardId);
            CREATE INDEX IF NOT EXISTS IX_BackupRestoreQueueItems_ClusterId_LogicalShardNumber ON BackupRestoreQueueItems (ClusterId, LogicalShardNumber);
            CREATE INDEX IF NOT EXISTS IX_BackupRestoreQueueItems_NodeHost_NodePort_NodeUseTls ON BackupRestoreQueueItems (NodeHost, NodePort, NodeUseTls);
            CREATE INDEX IF NOT EXISTS IX_BackupRestoreQueueItems_ClusterId_NodeHost_NodePort_NodeUseTls_StartedAt_CompletedAt ON BackupRestoreQueueItems (ClusterId, NodeHost, NodePort, NodeUseTls, StartedAt, CompletedAt);
            CREATE INDEX IF NOT EXISTS IX_BackupRestoreQueueItems_Kind_StartedAt_CompletedAt_IsForced_Position ON BackupRestoreQueueItems (Kind, StartedAt, CompletedAt, IsForced, Position);
            CREATE INDEX IF NOT EXISTS IX_Restores_Status ON Restores (Status);
            CREATE INDEX IF NOT EXISTS IX_Restores_BackupId ON Restores (BackupId);
            CREATE INDEX IF NOT EXISTS IX_Restores_TargetClusterId ON Restores (TargetClusterId);
            CREATE INDEX IF NOT EXISTS IX_Restores_CreatedAt ON Restores (CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_RestoreTables_RestoreId ON RestoreTables (RestoreId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTables_BackupTableId ON RestoreTables (BackupTableId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTableShards_RestoreTableId ON RestoreTableShards (RestoreTableId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTableShards_BackupTableShardId ON RestoreTableShards (BackupTableShardId);
            CREATE INDEX IF NOT EXISTS IX_RestoreTableShards_Status ON RestoreTableShards (Status);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS RestoreTableShards;
            DROP TABLE IF EXISTS BackupRestoreQueueItems;
            DROP TABLE IF EXISTS RestoreTables;
            DROP TABLE IF EXISTS Restores;
            DROP TABLE IF EXISTS BackupTableShards;
            DROP TABLE IF EXISTS BackupTables;
            DROP TABLE IF EXISTS Backups;
            DROP TABLE IF EXISTS SchemaDefinitions;
            DROP TABLE IF EXISTS BackupSchedules;
            DROP TABLE IF EXISTS BackupPolicies;
            DROP TABLE IF EXISTS BackupTargets;
            DROP TABLE IF EXISTS ClickHouseAccessNodes;
            DROP TABLE IF EXISTS ClickHouseClusters;
            DROP TABLE IF EXISTS SqliteSelfBackupStates;
            DROP TABLE IF EXISTS ApplicationLogEntries;
            DROP TABLE IF EXISTS AuditEntries;
            DROP TABLE IF EXISTS AccessTokens;
            DROP TABLE IF EXISTS Users;
            DROP TABLE IF EXISTS SchemaStates;
            """);
    }
}




