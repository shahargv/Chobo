using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("000000000002_PasswordProtectedBackups")]
public sealed class PasswordProtectedBackups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "PasswordMode", table: "BackupPolicies", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(name: "EncryptedBackupPassword", table: "BackupPolicies", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "EncryptedBackupPasswordKeyId", table: "BackupPolicies", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>(name: "CompressionMethod", table: "BackupPolicies", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>(name: "CompressionLevel", table: "BackupPolicies", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>(name: "EncryptedBackupPassword", table: "BackupTableShards", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "EncryptedBackupPasswordKeyId", table: "BackupTableShards", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PasswordMode", table: "BackupPolicies");
        migrationBuilder.DropColumn(name: "EncryptedBackupPassword", table: "BackupPolicies");
        migrationBuilder.DropColumn(name: "EncryptedBackupPasswordKeyId", table: "BackupPolicies");
        migrationBuilder.DropColumn(name: "CompressionMethod", table: "BackupPolicies");
        migrationBuilder.DropColumn(name: "CompressionLevel", table: "BackupPolicies");
        migrationBuilder.DropColumn(name: "EncryptedBackupPassword", table: "BackupTableShards");
        migrationBuilder.DropColumn(name: "EncryptedBackupPasswordKeyId", table: "BackupTableShards");
    }
}
