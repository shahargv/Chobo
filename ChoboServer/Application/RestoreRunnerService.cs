using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.Application;

public sealed class RestoreRunnerService(
    IServiceScopeFactory scopeFactory,
    ChoboDbContext db,
    IOptions<ChoboBackupRestoreOptions> options,
    AuditService audit,
    Serilog.ILogger logger)
{
    private readonly Serilog.ILogger _logger = logger.ForContext<RestoreRunnerService>();

    public async Task RunAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        var restore = await db.Restores
            .Include(x => x.TargetCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.Tables)
            .FirstOrDefaultAsync(x => x.Id == restoreId, cancellationToken);
        if (restore is null)
        {
            return;
        }
        if (restore.Status is RestoreRunStatus.Succeeded or RestoreRunStatus.Failed or RestoreRunStatus.Canceled)
        {
            return;
        }

        try
        {
            _logger.Information("Starting restore run {RestoreId}. Current status: {Status}.", restore.Id, restore.Status);
            ValidateRestore(restore);
            restore.Status = RestoreRunStatus.Running;
            restore.StartedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("started", AuditEntityType.Restore, restore.Id.ToString(), new { restore.BackupId, restore.TargetClusterId });

            var maxDop = EffectiveMaxDop(restore.TargetCluster!);
            _logger.Information("Executing restore {RestoreId} with effective maxdop {MaxDop} and {TableCount} table(s).", restore.Id, maxDop, restore.Tables.Count);
            using var semaphore = new SemaphoreSlim(maxDop, maxDop);
            var tasks = restore.Tables
                .Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running)
                .Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try { await RunTableAsync(restore.Id, table.Id, cancellationToken); }
                    finally { semaphore.Release(); }
                })
                .ToList();
            await Task.WhenAll(tasks);

            var hasFailedTables = await db.RestoreTables.AnyAsync(x => x.RestoreId == restore.Id && x.Status == RestoreTableStatus.Failed, cancellationToken);
            var tableCount = await db.RestoreTables.CountAsync(x => x.RestoreId == restore.Id, cancellationToken);
            restore.Status = hasFailedTables ? RestoreRunStatus.Failed : RestoreRunStatus.Succeeded;
            restore.CompletedAt = DateTimeOffset.UtcNow;
            restore.Error = restore.Status == RestoreRunStatus.Failed ? "One or more tables failed." : null;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Restore {RestoreId} finished with status {Status}.", restore.Id, restore.Status);
            await audit.RecordAsync(restore.Status == RestoreRunStatus.Succeeded ? "succeeded" : "failed", AuditEntityType.Restore, restore.Id.ToString(), new { tableCount });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Restore {RestoreId} failed.", restoreId);
            restore.Status = RestoreRunStatus.Failed;
            restore.Error = ex.Message;
            restore.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("failed", AuditEntityType.Restore, restore.Id.ToString(), new { error = ex.Message });
        }
    }

    private async Task RunTableAsync(Guid restoreId, Guid tableId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var scopedOptions = scope.ServiceProvider.GetRequiredService<IOptions<ChoboBackupRestoreOptions>>();

        var restore = await scopedDb.Restores
            .Include(x => x.TargetCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.Tables)
            .FirstAsync(x => x.Id == restoreId, cancellationToken);
        var table = restore.Tables.Single(x => x.Id == tableId);
        var backupTable = await scopedDb.BackupTables.Include(x => x.SchemaDefinition).FirstAsync(x => x.Id == table.BackupTableId, cancellationToken);

        table.Status = RestoreTableStatus.Running;
        table.StartedAt ??= DateTimeOffset.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);
        _logger.Information("Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} started.", table.Id, table.TargetDatabase, table.TargetTable);
        await scopedAudit.RecordAsync("table-started", AuditEntityType.RestoreTable, table.Id.ToString(), new { table.TargetDatabase, table.TargetTable });

        try
        {
            await scopedClickHouse.ExecuteAsync(restore.TargetCluster!, $"CREATE DATABASE IF NOT EXISTS {ClickHouseSql.Identifier(table.TargetDatabase)}", cancellationToken);
            var existing = await scopedClickHouse.GetTableAsync(restore.TargetCluster!, table.TargetDatabase, table.TargetTable, cancellationToken);
            if (existing is not null && !restore.Append)
            {
                throw new InvalidOperationException($"Target table {table.TargetDatabase}.{table.TargetTable} already exists.");
            }
            if (existing is not null && existing.SchemaHash != backupTable.SchemaDefinition!.SchemaHash)
            {
                if (!restore.AllowSchemaMismatch)
                {
                    throw new InvalidOperationException($"Target table {table.TargetDatabase}.{table.TargetTable} has a different schema.");
                }

                table.Warning = "Target schema differs from backup schema; continuing because allow schema mismatch was requested.";
            }
            if (existing is null && restore.Append)
            {
                throw new InvalidOperationException($"Append restore requires target table {table.TargetDatabase}.{table.TargetTable} to already exist.");
            }

            if (!backupTable.DataBackedUp)
            {
                if (existing is null)
                {
                    await scopedClickHouse.ExecuteAsync(restore.TargetCluster!, ClickHouseSql.RewriteCreateTableName(backupTable.SchemaDefinition!.CreateTableSql, table.TargetDatabase, table.TargetTable), cancellationToken);
                }
                table.Status = RestoreTableStatus.Succeeded;
                table.ClickHouseStatus = "SCHEMA_ONLY";
                table.CompletedAt = DateTimeOffset.UtcNow;
                await scopedDb.SaveChangesAsync(cancellationToken);
                _logger.Information("Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} completed as schema-only.", table.Id, table.TargetDatabase, table.TargetTable);
                await scopedAudit.RecordAsync("table-skipped", AuditEntityType.RestoreTable, table.Id.ToString(), new { reason = "schema-only" });
                return;
            }

            var restoreTargetDatabase = table.TargetDatabase;
            var restoreTargetTable = table.TargetTable;
            var finalTargetTable = table.TargetTable;
            string? tempTable = null;
            if (restore.Append)
            {
                tempTable = $"__chobo_restore_{table.Id:N}";
                restoreTargetTable = tempTable;
            }

            table.TargetDatabase = restoreTargetDatabase;
            table.TargetTable = restoreTargetTable;
            if (!string.IsNullOrWhiteSpace(table.ClickHouseOperationId))
            {
                var status = await scopedClickHouse.GetOperationStatusAsync(restore.TargetCluster!, table.ClickHouseOperationId, cancellationToken);
                if (status.Exists)
                {
                    await PollRestoreAsync(scopedClickHouse, restore.TargetCluster!, table, status, scopedOptions.Value.PollInterval, cancellationToken);
                }
                else
                {
                    await scopedAudit.RecordAsync("table-restarted", AuditEntityType.RestoreTable, table.Id.ToString(), new { reason = "operation-not-found", table.ClickHouseOperationId });
                    table.ClickHouseOperationId = null;
                    table.ClickHouseStatus = null;
                }
            }

            if (string.IsNullOrWhiteSpace(table.ClickHouseOperationId))
            {
                var operation = await scopedClickHouse.StartRestoreAsync(restore.TargetCluster!, restore.Backup!.Target!, table, backupTable, cancellationToken);
                table.ClickHouseOperationId = operation.OperationId;
                table.ClickHouseStatus = operation.Status;
                await scopedDb.SaveChangesAsync(cancellationToken);
                _logger.Information("Restore table {RestoreTableId} submitted ClickHouse operation {OperationId} status {Status}.", table.Id, operation.OperationId, operation.Status);
                await scopedAudit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.RestoreTable, table.Id.ToString(), new { operation.OperationId, operation.Status });
                await PollRestoreAsync(scopedClickHouse, restore.TargetCluster!, table, new ClickHouseOperationStatus(true, operation.Status, null), scopedOptions.Value.PollInterval, cancellationToken);
            }

            if (restore.Append && tempTable is not null)
            {
                var insertSql = table.Warning is null
                    ? $"INSERT INTO {ClickHouseSql.Qualified(restoreTargetDatabase, finalTargetTable)} SELECT * FROM {ClickHouseSql.Qualified(restoreTargetDatabase, tempTable)}"
                    : BuildMismatchAppendInsertSql(existing!, backupTable.SchemaDefinition!, restoreTargetDatabase, finalTargetTable, tempTable);
                await scopedClickHouse.ExecuteAsync(restore.TargetCluster!, insertSql, cancellationToken);
                await scopedClickHouse.ExecuteAsync(restore.TargetCluster!, $"DROP TABLE IF EXISTS {ClickHouseSql.Qualified(restoreTargetDatabase, tempTable)}", cancellationToken);
                table.TargetTable = finalTargetTable;
            }

            table.Status = RestoreTableStatus.Succeeded;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(cancellationToken);
            _logger.Information("Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} completed with ClickHouse status {Status}.", table.Id, table.TargetDatabase, table.TargetTable, table.ClickHouseStatus);
            await scopedAudit.RecordAsync("table-succeeded", AuditEntityType.RestoreTable, table.Id.ToString(), new { table.TargetDatabase, table.TargetTable });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} failed.", table.Id, table.TargetDatabase, table.TargetTable);
            table.Status = RestoreTableStatus.Failed;
            table.Error = ex.Message;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("table-failed", AuditEntityType.RestoreTable, table.Id.ToString(), new { error = ex.Message });
        }
    }

    private static async Task PollRestoreAsync(IClickHouseAdapter adapter, ClickHouseClusterEntity cluster, RestoreTableEntity table, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!current.Exists)
            {
                throw new InvalidOperationException("ClickHouse operation disappeared.");
            }

            table.ClickHouseStatus = current.Status;
            if (string.Equals(current.Status, "RESTORED", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (current.Status?.Contains("FAILED", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(current.Error) ? $"ClickHouse operation failed with status {current.Status}." : current.Error);
            }

            await Task.Delay(interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval, cancellationToken);
            current = await adapter.GetOperationStatusAsync(cluster, table.ClickHouseOperationId!, cancellationToken);
        }
    }

    private void ValidateRestore(RestoreEntity restore)
    {
        if (restore.TargetCluster is null || restore.Backup?.Target is null)
        {
            throw new InvalidOperationException("Restore target cluster or backup target was not found.");
        }
        if (restore.TargetCluster.Mode != ClusterMode.SingleInstance)
        {
            throw new InvalidOperationException("Only single instance ClickHouse clusters are supported in this iteration.");
        }
    }

    private int EffectiveMaxDop(ClickHouseClusterEntity cluster) =>
        Math.Max(1, cluster.BackupRestoreMaxDop is > 0 ? cluster.BackupRestoreMaxDop.Value : options.Value.MaxDop <= 0 ? 3 : options.Value.MaxDop);

    private static string BuildMismatchAppendInsertSql(ClickHouseTableInfo existing, SchemaDefinitionEntity backupSchema, string database, string targetTable, string tempTable)
    {
        var sourceColumns = ReadColumnNames(backupSchema.ColumnsJson).ToHashSet(StringComparer.Ordinal);
        var targetColumns = ReadColumnNames(existing.ColumnsJson).Where(sourceColumns.Contains).ToList();
        if (targetColumns.Count == 0)
        {
            throw new InvalidOperationException("Target table has no columns in common with the restored table.");
        }

        var columnList = string.Join(", ", targetColumns.Select(ClickHouseSql.Identifier));
        return $"INSERT INTO {ClickHouseSql.Qualified(database, targetTable)} ({columnList}) SELECT {columnList} FROM {ClickHouseSql.Qualified(database, tempTable)}";
    }

    private static IReadOnlyList<string> ReadColumnNames(string columnsJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(columnsJson);
        return document.RootElement.EnumerateArray()
            .Select(x => x.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }
}
