using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("20260511190000_ScheduleMissedRunGracePeriod")]
public sealed class ScheduleMissedRunGracePeriod : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE BackupSchedules ADD COLUMN MissedRunGracePeriod TEXT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
