using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("20260509160000_InitialCreate")]
public sealed class InitialCreate : Migration
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
                Details TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ApplicationLogEntries (
                Id INTEGER NOT NULL CONSTRAINT PK_ApplicationLogEntries PRIMARY KEY AUTOINCREMENT,
                Timestamp INTEGER,
                Level VARCHAR(10),
                Exception TEXT,
                RenderedMessage TEXT,
                Properties TEXT
            );

            CREATE TABLE IF NOT EXISTS ClickHouseClusters (
                Id TEXT NOT NULL CONSTRAINT PK_ClickHouseClusters PRIMARY KEY,
                Name TEXT NOT NULL,
                Mode INTEGER NOT NULL,
                EncryptedUserName TEXT NULL,
                EncryptedUserNameKeyId TEXT NULL,
                EncryptedPassword TEXT NULL,
                EncryptedPasswordKeyId TEXT NULL,
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
                SelectorJsonVersion INTEGER NOT NULL,
                SelectorJson TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NULL,
                DeletedAt INTEGER NULL,
                CONSTRAINT FK_BackupPolicies_ClickHouseClusters_SourceClusterId FOREIGN KEY (SourceClusterId) REFERENCES ClickHouseClusters (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS BackupSchedules (
                Id TEXT NOT NULL CONSTRAINT PK_BackupSchedules PRIMARY KEY,
                Name TEXT NOT NULL,
                PolicyId TEXT NOT NULL,
                BackupType INTEGER NOT NULL,
                CronExpression TEXT NOT NULL,
                TimeZoneId TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                Description TEXT NULL,
                IsDeleted INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NULL,
                DeletedAt INTEGER NULL,
                CONSTRAINT FK_BackupSchedules_BackupPolicies_PolicyId FOREIGN KEY (PolicyId) REFERENCES BackupPolicies (Id) ON DELETE CASCADE
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
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Timestamp ON ApplicationLogEntries (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Level_Timestamp ON ApplicationLogEntries (Level, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_ClickHouseClusters_IsDeleted_Name ON ClickHouseClusters (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_ClickHouseAccessNodes_ClusterId ON ClickHouseAccessNodes (ClusterId);
            CREATE INDEX IF NOT EXISTS IX_BackupTargets_IsDeleted_Name ON BackupTargets (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_IsDeleted_Name ON BackupPolicies (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_SourceClusterId ON BackupPolicies (SourceClusterId);
            CREATE INDEX IF NOT EXISTS IX_BackupSchedules_IsDeleted_Name ON BackupSchedules (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupSchedules_PolicyId_IsDeleted ON BackupSchedules (PolicyId, IsDeleted);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS BackupSchedules;
            DROP TABLE IF EXISTS BackupPolicies;
            DROP TABLE IF EXISTS BackupTargets;
            DROP TABLE IF EXISTS ClickHouseAccessNodes;
            DROP TABLE IF EXISTS ClickHouseClusters;
            DROP TABLE IF EXISTS ApplicationLogEntries;
            DROP TABLE IF EXISTS AuditEntries;
            DROP TABLE IF EXISTS AccessTokens;
            DROP TABLE IF EXISTS Users;
            DROP TABLE IF EXISTS SchemaStates;
            """);
    }
}
