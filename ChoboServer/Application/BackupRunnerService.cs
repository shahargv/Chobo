using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.Application;

public sealed class BackupRunnerService(
    IServiceScopeFactory scopeFactory,
    ChoboDbContext db,
    IClickHouseAdapter clickHouse,
    PolicySelectorEvaluationService selectorEvaluation,
    IOptions<ChoboBackupRestoreOptions> options,
    AuditService audit,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupRunnerService>();

    public async Task RunAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Target)
            .Include(x => x.Policy)
            .Include(x => x.Tables)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null)
        {
            return;
        }
        if (backup.Status == BackupRunStatus.Succeeded || backup.Status == BackupRunStatus.Failed || backup.Status == BackupRunStatus.Canceled)
        {
            return;
        }

        try
        {
            _logger.Information("Starting backup run {BackupId}. Current status: {Status}.", backup.Id, backup.Status);
            backup.Status = BackupRunStatus.Running;
            backup.StartedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("started", AuditEntityType.Backup, backup.Id.ToString(), new { backup.SourceClusterId, backup.TargetId });

            ValidateBackup(backup);
            if (backup.Tables.Count == 0)
            {
                _logger.Information("Preparing backup tables for backup {BackupId}.", backup.Id);
                await PrepareTablesAsync(backup, cancellationToken);
            }

            var maxDop = EffectiveMaxDop(backup.SourceCluster!);
            _logger.Information("Executing backup {BackupId} with effective maxdop {MaxDop} and {TableCount} table(s).", backup.Id, maxDop, backup.Tables.Count);
            if (maxDop == 1)
            {
                foreach (var table in backup.Tables.Where(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running).ToList())
                {
                    await RunTableInCurrentScopeAsync(backup, table, cancellationToken);
                }
            }
            else
            {
                using var semaphore = new SemaphoreSlim(maxDop, maxDop);
                var tasks = backup.Tables
                    .Where(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running)
                    .Select(async table =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try { await RunTableAsync(backup.Id, table.Id, cancellationToken); }
                        finally { semaphore.Release(); }
                    })
                    .ToList();
                await Task.WhenAll(tasks);
            }

            var hasFailedTables = await db.BackupTables.AnyAsync(x => x.BackupId == backup.Id && x.Status == BackupTableStatus.Failed, cancellationToken);
            var tableCount = await db.BackupTables.CountAsync(x => x.BackupId == backup.Id, cancellationToken);
            backup.Status = hasFailedTables ? BackupRunStatus.Failed : BackupRunStatus.Succeeded;
            backup.CompletedAt = DateTimeOffset.UtcNow;
            backup.Error = backup.Status == BackupRunStatus.Failed ? "One or more tables failed." : null;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup {BackupId} finished with status {Status}.", backup.Id, backup.Status);
            await audit.RecordAsync(backup.Status == BackupRunStatus.Succeeded ? "succeeded" : "failed", AuditEntityType.Backup, backup.Id.ToString(), new { tableCount });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup {BackupId} failed.", backupId);
            backup.Status = BackupRunStatus.Failed;
            backup.Error = ex.Message;
            backup.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("failed", AuditEntityType.Backup, backup.Id.ToString(), new { error = ex.Message });
        }
    }

    private async Task RunTableInCurrentScopeAsync(BackupEntity backup, BackupTableEntity table, CancellationToken cancellationToken)
    {
        if (!table.DataBackedUp)
        {
            _logger.Information("Backup table {BackupTableId} {Database}.{Table} marked schema-only; skipping data backup.", table.Id, table.Database, table.Table);
            table.Status = BackupTableStatus.Succeeded;
            table.ClickHouseStatus = "SCHEMA_ONLY";
            table.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("table-skipped", AuditEntityType.BackupTable, table.Id.ToString(), new { reason = "schema-only", table.Database, table.Table });
            return;
        }

        table.Status = BackupTableStatus.Running;
        table.StartedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Backup table {BackupTableId} {Database}.{Table} started.", table.Id, table.Database, table.Table);
        await audit.RecordAsync("table-started", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table });

        try
        {
            if (!string.IsNullOrWhiteSpace(table.ClickHouseOperationId))
            {
                var status = await clickHouse.GetOperationStatusAsync(backup.SourceCluster!, table.ClickHouseOperationId, cancellationToken);
                if (status.Exists)
                {
                    _logger.Information("Backup table {BackupTableId} resuming ClickHouse operation {OperationId}.", table.Id, table.ClickHouseOperationId);
                    await PollBackupAsync(clickHouse, backup.SourceCluster!, table, status, options.Value.PollInterval, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.Information("Backup table {BackupTableId} {Database}.{Table} completed with ClickHouse status {Status}.", table.Id, table.Database, table.Table, table.ClickHouseStatus);
                    await audit.RecordAsync("table-succeeded", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, table.ClickHouseStatus });
                    return;
                }

                await audit.RecordAsync("table-restarted", AuditEntityType.BackupTable, table.Id.ToString(), new { reason = "operation-not-found", table.ClickHouseOperationId });
                table.ClickHouseOperationId = null;
                table.ClickHouseStatus = null;
            }

            var operation = await clickHouse.StartBackupAsync(backup.SourceCluster!, backup.Target!, table, cancellationToken);
            table.ClickHouseOperationId = operation.OperationId;
            table.ClickHouseStatus = operation.Status;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup table {BackupTableId} submitted ClickHouse operation {OperationId} status {Status}.", table.Id, operation.OperationId, operation.Status);
            await audit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.BackupTable, table.Id.ToString(), new { operation.OperationId, operation.Status });
            await PollBackupAsync(clickHouse, backup.SourceCluster!, table, new ClickHouseOperationStatus(true, operation.Status, null), options.Value.PollInterval, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup table {BackupTableId} {Database}.{Table} completed with ClickHouse status {Status}.", table.Id, table.Database, table.Table, table.ClickHouseStatus);
            await audit.RecordAsync("table-succeeded", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, table.ClickHouseStatus });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup table {BackupTableId} {Database}.{Table} failed.", table.Id, table.Database, table.Table);
            table.Status = BackupTableStatus.Failed;
            table.Error = ex.Message;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("table-failed", AuditEntityType.BackupTable, table.Id.ToString(), new { error = ex.Message });
        }
    }

    private async Task PrepareTablesAsync(BackupEntity backup, CancellationToken cancellationToken)
    {
        var selector = backup.Policy is not null
            ? JsonSerializer.Deserialize<PolicySelector>(backup.Policy.SelectorJson, JsonOptions) ?? PolicySelector.Empty
            : JsonSerializer.Deserialize<ManualBackupRequest>(backup.ManualRequestJson ?? "", JsonOptions)?.Selector ?? PolicySelector.Empty;

        var inventory = await clickHouse.GetTablesAsync(backup.SourceCluster!, cancellationToken);
        _logger.Information("Backup {BackupId} inventory contains {InventoryCount} table(s).", backup.Id, inventory.Count);
        var selectableInventory = inventory
            .Where(x => !IsExcludedSystemDatabase(x.Database))
            .ToList();
        var selected = selectorEvaluation.Evaluate(selector, new PolicyInventory(selectableInventory.Select(x => new PolicyInventoryTable(x.Database, x.Table)).ToList()));
        _logger.Information("Backup {BackupId} selector matched {SelectedCount} table(s).", backup.Id, selected.Count);
        var selectedSet = selected.Select(x => $"{x.Database}.{x.Table}").ToHashSet(StringComparer.Ordinal);

        foreach (var table in selectableInventory.Where(x => selectedSet.Contains($"{x.Database}.{x.Table}")))
        {
            var schema = await db.SchemaDefinitions.FirstOrDefaultAsync(x => x.SchemaHash == table.SchemaHash, cancellationToken);
            if (schema is null)
            {
                schema = new SchemaDefinitionEntity
                {
                    SchemaHash = table.SchemaHash,
                    Database = table.Database,
                    Table = table.Table,
                    Engine = table.Engine,
                    CreateTableSql = table.CreateTableSql,
                    ColumnsJson = table.ColumnsJson
                };
                db.SchemaDefinitions.Add(schema);
                await db.SaveChangesAsync(cancellationToken);
                _logger.Information("Stored schema definition {SchemaDefinitionId} for {Database}.{Table} hash {SchemaHash}.", schema.Id, table.Database, table.Table, table.SchemaHash);
            }

            var dataBackedUp = IsMergeTreeDataEngine(table.Engine);
            if (IsReplicatedMergeTree(table.Engine))
            {
                throw new InvalidOperationException($"Replicated MergeTree table {table.Database}.{table.Table} is not supported in this iteration.");
            }

            db.BackupTables.Add(new BackupTableEntity
            {
                BackupId = backup.Id,
                Database = table.Database,
                Table = table.Table,
                Engine = table.Engine,
                DataBackedUp = dataBackedUp,
                SchemaDefinitionId = schema.Id,
                S3Path = BuildS3Path(backup, table.Database, table.Table)
            });
            _logger.Information("Prepared backup table {Database}.{Table} engine {Engine} dataBackedUp={DataBackedUp}.", table.Database, table.Table, table.Engine, dataBackedUp);
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(backup).Collection(x => x.Tables).LoadAsync(cancellationToken);
        _logger.Information("Backup {BackupId} prepared {TableCount} table row(s).", backup.Id, backup.Tables.Count);
        await audit.RecordAsync("tables-prepared", AuditEntityType.Backup, backup.Id.ToString(), new { tableCount = backup.Tables.Count });
    }

    private async Task RunTableAsync(Guid backupId, Guid tableId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var scopedOptions = scope.ServiceProvider.GetRequiredService<IOptions<ChoboBackupRestoreOptions>>();

        var backup = await scopedDb.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Target)
            .Include(x => x.Tables)
            .FirstAsync(x => x.Id == backupId, cancellationToken);
        var table = backup.Tables.Single(x => x.Id == tableId);

        if (!table.DataBackedUp)
        {
            _logger.Information("Backup table {BackupTableId} {Database}.{Table} marked schema-only; skipping data backup.", table.Id, table.Database, table.Table);
            table.Status = BackupTableStatus.Succeeded;
            table.ClickHouseStatus = "SCHEMA_ONLY";
            table.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("table-skipped", AuditEntityType.BackupTable, table.Id.ToString(), new { reason = "schema-only", table.Database, table.Table });
            return;
        }

        table.Status = BackupTableStatus.Running;
        table.StartedAt ??= DateTimeOffset.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);
        _logger.Information("Backup table {BackupTableId} {Database}.{Table} started.", table.Id, table.Database, table.Table);
        await scopedAudit.RecordAsync("table-started", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table });

        try
        {
            if (!string.IsNullOrWhiteSpace(table.ClickHouseOperationId))
            {
                var status = await scopedClickHouse.GetOperationStatusAsync(backup.SourceCluster!, table.ClickHouseOperationId, cancellationToken);
                if (status.Exists)
                {
                    _logger.Information("Backup table {BackupTableId} resuming ClickHouse operation {OperationId}.", table.Id, table.ClickHouseOperationId);
                    await PollBackupAsync(scopedClickHouse, backup.SourceCluster!, table, status, scopedOptions.Value.PollInterval, cancellationToken);
                    await scopedDb.SaveChangesAsync(cancellationToken);
                    _logger.Information("Backup table {BackupTableId} {Database}.{Table} completed with ClickHouse status {Status}.", table.Id, table.Database, table.Table, table.ClickHouseStatus);
                    await scopedAudit.RecordAsync("table-succeeded", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, table.ClickHouseStatus });
                    return;
                }

                await scopedAudit.RecordAsync("table-restarted", AuditEntityType.BackupTable, table.Id.ToString(), new { reason = "operation-not-found", table.ClickHouseOperationId });
                table.ClickHouseOperationId = null;
                table.ClickHouseStatus = null;
            }

            var operation = await scopedClickHouse.StartBackupAsync(backup.SourceCluster!, backup.Target!, table, cancellationToken);
            table.ClickHouseOperationId = operation.OperationId;
            table.ClickHouseStatus = operation.Status;
            await scopedDb.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup table {BackupTableId} submitted ClickHouse operation {OperationId} status {Status}.", table.Id, operation.OperationId, operation.Status);
            await scopedAudit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.BackupTable, table.Id.ToString(), new { operation.OperationId, operation.Status });
            await PollBackupAsync(scopedClickHouse, backup.SourceCluster!, table, new ClickHouseOperationStatus(true, operation.Status, null), scopedOptions.Value.PollInterval, cancellationToken);
            await scopedDb.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup table {BackupTableId} {Database}.{Table} completed with ClickHouse status {Status}.", table.Id, table.Database, table.Table, table.ClickHouseStatus);
            await scopedAudit.RecordAsync("table-succeeded", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, table.ClickHouseStatus });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup table {BackupTableId} {Database}.{Table} failed.", table.Id, table.Database, table.Table);
            table.Status = BackupTableStatus.Failed;
            table.Error = ex.Message;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("table-failed", AuditEntityType.BackupTable, table.Id.ToString(), new { error = ex.Message });
        }
    }

    private static async Task PollBackupAsync(IClickHouseAdapter adapter, ClickHouseClusterEntity cluster, BackupTableEntity table, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!current.Exists)
            {
                throw new InvalidOperationException("ClickHouse operation disappeared.");
            }

            table.ClickHouseStatus = current.Status;
            if (IsSuccessStatus(current.Status))
            {
                table.Status = BackupTableStatus.Succeeded;
                table.CompletedAt = DateTimeOffset.UtcNow;
                return;
            }
            if (IsFailedStatus(current.Status))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(current.Error) ? $"ClickHouse operation failed with status {current.Status}." : current.Error);
            }

            await Task.Delay(interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval, cancellationToken);
            current = await adapter.GetOperationStatusAsync(cluster, table.ClickHouseOperationId!, cancellationToken);
        }
    }

    private void ValidateBackup(BackupEntity backup)
    {
        if (backup.BackupType != BackupType.Full)
        {
            throw new InvalidOperationException("Only full backups are supported in this iteration.");
        }
        if (backup.SourceCluster is null || backup.Target is null)
        {
            throw new InvalidOperationException("Backup source cluster or target was not found.");
        }
        if (backup.SourceCluster.Mode != ClusterMode.SingleInstance)
        {
            throw new InvalidOperationException("Only single instance ClickHouse clusters are supported in this iteration.");
        }
    }

    private int EffectiveMaxDop(ClickHouseClusterEntity cluster) =>
        Math.Max(1, cluster.BackupRestoreMaxDop is > 0 ? cluster.BackupRestoreMaxDop.Value : options.Value.MaxDop <= 0 ? 3 : options.Value.MaxDop);

    private static bool IsMergeTreeDataEngine(string engine) =>
        engine.Contains("MergeTree", StringComparison.OrdinalIgnoreCase) && !IsReplicatedMergeTree(engine);

    private static bool IsReplicatedMergeTree(string engine) =>
        engine.StartsWith("Replicated", StringComparison.OrdinalIgnoreCase) && engine.Contains("MergeTree", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedSystemDatabase(string database) =>
        string.Equals(database, "system", StringComparison.Ordinal) ||
        string.Equals(database, "information_schema", StringComparison.Ordinal) ||
        string.Equals(database, "INFORMATION_SCHEMA", StringComparison.Ordinal);

    private static string BuildS3Path(BackupEntity backup, string database, string table)
    {
        var source = backup.PolicyId is { } policyId ? $"policy-{policyId:N}" : "manual";
        var type = backup.BackupType.ToString().ToLowerInvariant();
        var timestamp = backup.CreatedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        return $"backups/{EscapePathPart(database)}/{EscapePathPart(table)}/{source}/{type}/{timestamp}/{backup.Id:N}";
    }

    private static string EscapePathPart(string value) =>
        Uri.EscapeDataString(value).Replace("%2F", "_", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessStatus(string? status) =>
        string.Equals(status, "BACKUP_CREATED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "RESTORED", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedStatus(string? status) =>
        status?.Contains("FAILED", StringComparison.OrdinalIgnoreCase) == true;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
