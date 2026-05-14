using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("20260514120000_BackupRetentionCleanup")]
public sealed class BackupRetentionCleanup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE BackupPolicies ADD COLUMN RetentionMinutes INTEGER NULL;
            ALTER TABLE BackupPolicies ADD COLUMN MinBackupsToKeep INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE BackupPolicies ADD COLUMN FailedBackupRetentionMode INTEGER NOT NULL DEFAULT 0;

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

            CREATE INDEX IF NOT EXISTS IX_Backups_IsPinned ON Backups (IsPinned);
            CREATE INDEX IF NOT EXISTS IX_Backups_DeletionRequestedAt ON Backups (DeletionRequestedAt);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
