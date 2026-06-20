using System.Text;
using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class SchemaBrowserApplicationService(ChoboDbContext db)
{
    public async Task<IReadOnlyList<SchemaBackupSummaryDto>> ListBackupsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var query = db.Backups
            .Include(x => x.SourceCluster)
            .Where(x => x.Status == BackupRunStatus.Succeeded || x.Status == BackupRunStatus.PartiallySucceeded)
            .Where(x => x.Tables.Any(t => t.SchemaDefinitionId != null));

        if (from is not null)
        {
            query = query.Where(x => (x.CompletedAt ?? x.CreatedAt) >= from.Value);
        }
        if (to is not null)
        {
            query = query.Where(x => (x.CompletedAt ?? x.CreatedAt) <= to.Value);
        }

        return await query
            .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
            .Select(x => new SchemaBackupSummaryDto(x.Id, x.Status, x.ContentMode, x.BackupType, x.SourceClusterId, x.SourceCluster == null ? x.SourceClusterId.ToString() : x.SourceCluster.Name, x.PolicyId, x.CreatedAt, x.CompletedAt, x.Tables.Count(t => t.SchemaDefinitionId != null)))
            .Take(200)
            .ToListAsync(cancellationToken);
    }

    public async Task<SchemaBackupDto?> GetBackupSchemaAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.Tables).ThenInclude(x => x.SchemaDefinition)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null)
        {
            return null;
        }

        var databases = backup.Tables
            .Where(x => x.SchemaDefinition is not null)
            .OrderBy(x => x.Database, StringComparer.Ordinal)
            .ThenBy(x => x.Table, StringComparer.Ordinal)
            .GroupBy(x => x.Database, StringComparer.Ordinal)
            .Select(group => new SchemaDatabaseDto(
                group.Key,
                group.Select(table => new SchemaTableDto(
                    table.Id,
                    table.Database,
                    table.Table,
                    table.Engine,
                    table.DataBackedUp,
                    table.SchemaDefinition!.CreateTableSql,
                    table.SchemaDefinition.ColumnsJson)).ToList()))
            .ToList();

        return new SchemaBackupDto(backup.Id, backup.Status, backup.ContentMode, databases);
    }

    public async Task<string?> ExportSqlAsync(Guid backupId, string? database, CancellationToken cancellationToken = default)
    {
        var schema = await GetBackupSchemaAsync(backupId, cancellationToken);
        if (schema is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var dbGroup in schema.Databases.Where(x => string.IsNullOrWhiteSpace(database) || string.Equals(x.Database, database, StringComparison.Ordinal)).OrderBy(x => x.Database, StringComparer.Ordinal))
        {
            builder.AppendLine($"-- Database: {dbGroup.Database}");
            foreach (var table in dbGroup.Tables.OrderBy(x => x.Table, StringComparer.Ordinal))
            {
                builder.AppendLine(table.CreateTableSql.Trim().TrimEnd(';') + ";");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }
}
