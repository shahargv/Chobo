using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace ChoboServer.Application;

public sealed class BackupRunnerService(
    IServiceScopeFactory scopeFactory,
    ChoboDbContext db,
    IClickHouseAdapter clickHouse,
    PolicySelectorEvaluationService selectorEvaluation,
    IOptions<ChoboBackupRestoreOptions> options,
    IBackupStorageManifestService manifests,
    IAuditService audit,
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
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null)
        {
            return;
        }
        if (backup.Status is BackupRunStatus.Succeeded or
            BackupRunStatus.Failed or
            BackupRunStatus.Canceled or
            BackupRunStatus.ManualDeleteRequested or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeleteRequested or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleteStarted or
            BackupRunStatus.BackupExpiredDeleted)
        {
            return;
        }

        using var operationCorrelationScope = OperationCorrelationContext.Push(backup.Id.ToString());
        using var operationLogScope = LogContext.PushProperty("OperationId", backup.Id.ToString());

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
                await TryWriteIntermediateManifestAsync(backup.Id, cancellationToken);
            }

            var maxDop = EffectiveMaxDop(backup.SourceCluster!);
            _logger.Information("Executing backup {BackupId} with effective maxdop {MaxDop} and {TableCount} table(s).", backup.Id, maxDop, backup.Tables.Count);
            if (maxDop == 1)
            {
                foreach (var table in backup.Tables.Where(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running).ToList())
                {
                    await RunTableAsync(backup.Id, table.Id, cancellationToken);
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

            if (await db.Backups.Where(x => x.Id == backup.Id).Select(x => x.Status).FirstAsync(cancellationToken) == BackupRunStatus.Canceled)
            {
                _logger.Information("Backup {BackupId} observed cancellation and will not overwrite canceled status.", backup.Id);
                return;
            }

            var statuses = await db.BackupTables.Where(x => x.BackupId == backup.Id).Select(x => x.Status).ToListAsync(cancellationToken);
            var tableCount = await db.BackupTables.CountAsync(x => x.BackupId == backup.Id, cancellationToken);
            backup.Status = AggregateBackupStatus(statuses);
            backup.CompletedAt = DateTimeOffset.UtcNow;
            backup.FailureReason = backup.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded
                ? await GetBackupFailureReasonAsync(backup.Id, cancellationToken)
                : null;
            backup.Error = backup.FailureReason;
            await db.SaveChangesAsync(cancellationToken);
            if (backup.Status == BackupRunStatus.Succeeded)
            {
                await WriteFinalManifestOrFailAsync(backup, tableCount, cancellationToken);
            }
            else
            {
                await TryWriteFailedManifestAsync(backup.Id);
            }
            _logger.Information("Backup {BackupId} finished with status {Status}. Failure reason: {FailureReason}.", backup.Id, backup.Status, backup.FailureReason);
            var auditAction = backup.Status == BackupRunStatus.Succeeded ? "succeeded" : backup.Status == BackupRunStatus.PartiallySucceeded ? "partially-succeeded" : "failed";
            await audit.RecordAsync(auditAction, AuditEntityType.Backup, backup.Id.ToString(), new { tableCount, backup.FailureReason });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup {BackupId} failed.", backupId);
            backup.Status = BackupRunStatus.Failed;
            backup.Error = ex.Message;
            backup.FailureReason = ex.Message;
            backup.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await TryWriteFailedManifestAsync(backup.Id);
            await audit.RecordAsync("failed", AuditEntityType.Backup, backup.Id.ToString(), new { error = ex.Message, backup.FailureReason });
        }
    }

    private async Task WriteFinalManifestOrFailAsync(BackupEntity backup, int tableCount, CancellationToken cancellationToken)
    {
        try
        {
            await manifests.WriteManifestAsync(backup.Id, cancellationToken);
            await audit.RecordAsync("metadata-manifest-written", AuditEntityType.Backup, backup.Id.ToString(), new { tableCount });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Final backup storage manifest write failed for backup {BackupId}.", backup.Id);
            backup.Status = BackupRunStatus.Failed;
            backup.Error = ex.Message;
            backup.FailureReason = $"Backup metadata manifest write failed: {ex.Message}";
            backup.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("metadata-manifest-write-failed", AuditEntityType.Backup, backup.Id.ToString(), new { error = ex.Message });
            throw;
        }
    }

    private async Task TryWriteIntermediateManifestAsync(Guid backupId, CancellationToken cancellationToken)
    {
        try
        {
            await manifests.WriteManifestAsync(backupId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Intermediate backup storage manifest write failed for backup {BackupId}.", backupId);
        }
    }

    private async Task TryWriteFailedManifestAsync(Guid backupId)
    {
        try
        {
            await manifests.WriteManifestAsync(backupId, CancellationToken.None);
        }
        catch (Exception manifestException)
        {
            _logger.Warning(manifestException, "Failed-backup storage manifest write failed for backup {BackupId}.", backupId);
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
                    await audit.RecordAsync("table-succeeded", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, clickHouseOperationId = table.ClickHouseOperationId, table.ClickHouseStatus });
                    return;
                }

                throw MissingOperationException(table.ClickHouseOperationId);
            }

            var baseBackupPath = await GetParentTablePathAsync(table.ParentFullBackupTableId, cancellationToken);
            var operation = await clickHouse.StartBackupAsync(backup.SourceCluster!, backup.Target!, table, baseBackupPath, cancellationToken);
            table.ClickHouseOperationId = operation.OperationId;
            table.ClickHouseStatus = operation.Status;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup table {BackupTableId} submitted ClickHouse operation {OperationId} status {Status}.", table.Id, operation.OperationId, operation.Status);
            await audit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.BackupTable, table.Id.ToString(), new { clickHouseOperationId = operation.OperationId, operation.Status });
            await PollBackupAsync(clickHouse, backup.SourceCluster!, table, new ClickHouseOperationStatus(true, operation.Status, null), options.Value.PollInterval, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup table {BackupTableId} {Database}.{Table} completed with ClickHouse status {Status}.", table.Id, table.Database, table.Table, table.ClickHouseStatus);
            await audit.RecordAsync("table-succeeded", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, clickHouseOperationId = table.ClickHouseOperationId, table.ClickHouseStatus });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup table {BackupTableId} {Database}.{Table} failed.", table.Id, table.Database, table.Table);
            table.Status = BackupTableStatus.Failed;
            table.Error = ex.Message;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("table-failed", AuditEntityType.BackupTable, table.Id.ToString(), new { error = ex.Message, clickHouseOperationId = table.ClickHouseOperationId });
        }
    }

    private async Task PrepareTablesAsync(BackupEntity backup, CancellationToken cancellationToken)
    {
        var manualRequest = backup.Policy is null
            ? JsonSerializer.Deserialize<ManualBackupRequest>(backup.ManualRequestJson ?? "", JsonOptions)
            : null;
        var selector = backup.Policy is not null
            ? JsonSerializer.Deserialize<PolicySelector>(backup.Policy.SelectorJson, JsonOptions) ?? PolicySelector.Empty
            : manualRequest?.Selector ?? PolicySelector.Empty;
        var schemaOnly = manualRequest?.SchemaOnly ?? false;

        var topology = await clickHouse.GetTopologyAsync(backup.SourceCluster!, cancellationToken);
        var representatives = SelectShardRepresentatives(topology);
        var inventory = await clickHouse.GetTablesAsync(backup.SourceCluster!, cancellationToken);
        _logger.Information("Backup {BackupId} inventory contains {InventoryCount} table(s).", backup.Id, inventory.Count);
        var selectableInventory = inventory
            .Where(x => !IsExcludedSystemDatabase(x.Database))
            .ToList();
        var selected = selectorEvaluation.Evaluate(selector, new PolicyInventory(selectableInventory.Select(x => new PolicyInventoryTable(x.Database, x.Table)).ToList()));
        _logger.Information("Backup {BackupId} selector matched {SelectedCount} table(s).", backup.Id, selected.Count);
        var selectedSet = selected.Select(x => ClickHouseBackupIdentity.Table(x.Database, x.Table)).ToHashSet(StringComparer.Ordinal);

        foreach (var table in selectableInventory.Where(x => selectedSet.Contains(ClickHouseBackupIdentity.Table(x.Database, x.Table))))
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

            var dataBackedUp = !schemaOnly && IsMergeTreeDataEngine(table.Engine);
            var parentTable = backup.BackupType == BackupType.Incremental && backup.PolicyId is not null && dataBackedUp
                ? await FindParentFullTableAsync(backup.PolicyId.Value, table.Database, table.Table, cancellationToken)
                : null;
            var effectiveTableType = parentTable is null ? BackupType.Full : BackupType.Incremental;
            var backupTable = new BackupTableEntity
            {
                BackupId = backup.Id,
                EffectiveBackupType = effectiveTableType,
                ParentFullBackupId = parentTable?.BackupId,
                ParentFullBackupTableId = parentTable?.Id,
                Database = table.Database,
                Table = table.Table,
                Engine = table.Engine,
                DataBackedUp = dataBackedUp,
                SchemaDefinitionId = schema.Id,
                S3Path = BuildS3Path(backup, table.Database, table.Table, effectiveTableType, parentTable?.BackupId)
            };
            if (backup.BackupType == BackupType.Incremental && dataBackedUp && parentTable is null)
            {
                await audit.RecordAsync("incremental-table-fallback-to-full", AuditEntityType.Backup, backup.Id.ToString(), new { table.Database, table.Table, reason = "missing-parent-full-table" });
            }
            if (dataBackedUp)
            {
                var parentShardsByIdentity = parentTable?.Shards.ToDictionary(
                    x => ClickHouseBackupIdentity.Shard(parentTable.Database, parentTable.Table, x.SourceShardNumber),
                    StringComparer.Ordinal);
                foreach (var representative in representatives)
                {
                    BackupTableShardEntity? parentShard = null;
                    parentShardsByIdentity?.TryGetValue(ClickHouseBackupIdentity.Shard(table.Database, table.Table, representative.ShardNumber), out parentShard);
                    var effectiveShardType = parentShard is null ? BackupType.Full : BackupType.Incremental;
                    if (backup.BackupType == BackupType.Incremental && parentTable is not null && parentShard is null)
                    {
                        await audit.RecordAsync("incremental-shard-fallback-to-full", AuditEntityType.Backup, backup.Id.ToString(), new { table.Database, table.Table, sourceShard = representative.ShardNumber, reason = "missing-parent-full-shard" });
                    }
                    var shardPath = BuildShardS3Path(
                        BuildS3Path(backup, table.Database, table.Table, effectiveShardType, parentShard?.BackupTable?.BackupId ?? parentTable?.BackupId),
                        representative.ShardNumber);
                    backupTable.Shards.Add(new BackupTableShardEntity
                    {
                        EffectiveBackupType = effectiveShardType,
                        ParentFullBackupId = parentShard is null ? null : parentTable?.BackupId,
                        ParentFullBackupTableShardId = parentShard?.Id,
                        SourceShardNumber = representative.ShardNumber,
                        SourceShardName = representative.ShardName,
                        ReplicaNumber = representative.ReplicaNumber,
                        Host = representative.Host,
                        Port = representative.Port,
                        UseTls = representative.UseTls,
                        S3Path = shardPath
                    });
                }
            }

            db.BackupTables.Add(backupTable);
            _logger.Information("Prepared backup table {Database}.{Table} engine {Engine} dataBackedUp={DataBackedUp}.", table.Database, table.Table, table.Engine, dataBackedUp);
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(backup).Collection(x => x.Tables).LoadAsync(cancellationToken);
        _logger.Information("Backup {BackupId} prepared {TableCount} table row(s).", backup.Id, backup.Tables.Count);
        var shardCount = await db.BackupTableShards.CountAsync(x => x.BackupTable!.BackupId == backup.Id, cancellationToken);
        await audit.RecordAsync("tables-prepared", AuditEntityType.Backup, backup.Id.ToString(), new { tableCount = backup.Tables.Count, shardCount });
        await audit.RecordAsync("shards-prepared", AuditEntityType.Backup, backup.Id.ToString(), new { shardCount });
    }

    private async Task RunTableAsync(Guid backupId, Guid tableId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var scopedOptions = scope.ServiceProvider.GetRequiredService<IOptions<ChoboBackupRestoreOptions>>();
        var scopedTestHooks = scope.ServiceProvider.GetRequiredService<ITestHookCoordinator>();
        var scopedManifests = scope.ServiceProvider.GetRequiredService<IBackupStorageManifestService>();

        var backup = await scopedDb.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Target)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstAsync(x => x.Id == backupId, cancellationToken);
        var table = backup.Tables.Single(x => x.Id == tableId);
        if (backup.Status == BackupRunStatus.Canceled)
        {
            _logger.Information("Backup table {BackupTableId} skipped because backup {BackupId} was canceled.", table.Id, backup.Id);
            return;
        }


        if (!table.DataBackedUp)
        {
            _logger.Information("Backup table {BackupTableId} {Database}.{Table} marked schema-only; skipping data backup.", table.Id, table.Database, table.Table);
            table.Status = BackupTableStatus.Succeeded;
            table.ClickHouseStatus = "SCHEMA_ONLY";
            table.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("table-skipped", AuditEntityType.BackupTable, table.Id.ToString(), new { reason = "schema-only", table.Database, table.Table });
            await TryWriteIntermediateManifestAsync(scopedManifests, backup.Id, cancellationToken);
            return;
        }

        if (table.Shards.Count == 0)
        {
            table.Shards.Add(new BackupTableShardEntity
            {
                SourceShardNumber = 1,
                SourceShardName = "single",
                ReplicaNumber = 1,
                Host = backup.SourceCluster!.AccessNodes[0].Host,
                Port = backup.SourceCluster.AccessNodes[0].Port,
                UseTls = backup.SourceCluster.AccessNodes[0].UseTls,
                S3Path = table.S3Path
            });
            await scopedDb.SaveChangesAsync(cancellationToken);
        }

        table.Status = BackupTableStatus.Running;
        table.StartedAt ??= DateTimeOffset.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);
        _logger.Information("Backup table {BackupTableId} {Database}.{Table} started.", table.Id, table.Database, table.Table);
        await scopedAudit.RecordAsync("table-started", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table });

        foreach (var shard in table.Shards.Where(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running).OrderBy(x => x.SourceShardNumber).ToList())
        {
            if (await scopedDb.Backups.Where(x => x.Id == backup.Id).Select(x => x.Status).FirstAsync(cancellationToken) == BackupRunStatus.Canceled)
            {
                _logger.Information("Backup table {BackupTableId} stopped before shard {ShardNumber} because backup {BackupId} was canceled.", table.Id, shard.SourceShardNumber, backup.Id);
                return;
            }
            shard.Status = BackupTableStatus.Running;
            shard.StartedAt ??= DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("shard-started", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, sourceNode = $"{shard.Host}:{shard.Port}" });

            try
            {
                var endpoint = new ClickHouseNodeEndpoint(shard.Host, shard.Port, shard.UseTls);
                if (!string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
                {
                    var status = await scopedClickHouse.GetOperationStatusAsync(endpoint, backup.SourceCluster!, shard.ClickHouseOperationId, cancellationToken);
                    if (status.Exists)
                    {
                        await PollBackupShardAsync(scopedClickHouse, endpoint, backup.SourceCluster!, shard, status, scopedOptions.Value.PollInterval, cancellationToken);
                        await scopedDb.SaveChangesAsync(cancellationToken);
                        await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, shard.ClickHouseStatus });
                        await TryWriteIntermediateManifestAsync(scopedManifests, backup.Id, cancellationToken);
                        continue;
                    }

                    throw MissingOperationException(shard.ClickHouseOperationId);
                }

                var baseBackupPath = await GetParentShardPathAsync(scopedDb, shard.ParentFullBackupTableShardId, cancellationToken);
                var operation = await scopedClickHouse.StartBackupShardAsync(endpoint, backup.SourceCluster!, backup.Target!, table, shard, baseBackupPath, cancellationToken);
                shard.ClickHouseOperationId = operation.OperationId;
                shard.ClickHouseStatus = operation.Status;
                table.ClickHouseOperationId ??= operation.OperationId;
                table.ClickHouseStatus = operation.Status;
                await scopedDb.SaveChangesAsync(cancellationToken);
                await scopedAudit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { clickHouseOperationId = operation.OperationId, operation.Status, sourceShard = shard.SourceShardNumber });
                await scopedTestHooks.MaybeDelayBackupBeforePollAsync(cancellationToken);
                await PollBackupShardAsync(scopedClickHouse, endpoint, backup.SourceCluster!, shard, new ClickHouseOperationStatus(true, operation.Status, null), scopedOptions.Value.PollInterval, cancellationToken);
                await scopedDb.SaveChangesAsync(cancellationToken);
                await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, shard.ClickHouseStatus });
                await TryWriteIntermediateManifestAsync(scopedManifests, backup.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Backup table {BackupTableId} {Database}.{Table} shard {ShardNumber} failed.", table.Id, table.Database, table.Table, shard.SourceShardNumber);
                shard.Status = BackupTableStatus.Failed;
                shard.Error = ex.Message;
                shard.CompletedAt = DateTimeOffset.UtcNow;
                await scopedDb.SaveChangesAsync(CancellationToken.None);
                await scopedAudit.RecordAsync("shard-failed", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = ex.Message, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId });
                await TryWriteIntermediateManifestAsync(scopedManifests, backup.Id, CancellationToken.None);
            }
        }

        table.Status = AggregateBackupTableStatus(table.Shards.Select(x => x.Status).ToList());
        table.ClickHouseStatus = table.Shards.Any(x => x.Status == BackupTableStatus.Failed) ? "PARTIAL_OR_FAILED" : table.Shards.LastOrDefault()?.ClickHouseStatus;
        table.Error = table.Status is BackupTableStatus.Failed or BackupTableStatus.PartiallySucceeded
            ? BuildBackupShardFailureReason(table.Shards.Where(x => x.Status == BackupTableStatus.Failed).ToList())
            : null;
        table.CompletedAt = DateTimeOffset.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);
        var tableAction = table.Status == BackupTableStatus.Succeeded ? "table-succeeded" : table.Status == BackupTableStatus.PartiallySucceeded ? "table-partially-succeeded" : "table-failed";
        await scopedAudit.RecordAsync(tableAction, AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, shardCount = table.Shards.Count, clickHouseOperationId = table.ClickHouseOperationId, failureReason = table.Error });
        await TryWriteIntermediateManifestAsync(scopedManifests, backup.Id, cancellationToken);
    }

    private async Task TryWriteIntermediateManifestAsync(IBackupStorageManifestService scopedManifests, Guid backupId, CancellationToken cancellationToken)
    {
        try
        {
            await scopedManifests.WriteManifestAsync(backupId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Intermediate backup storage manifest write failed for backup {BackupId}.", backupId);
        }
    }

    private async Task<string> GetBackupFailureReasonAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var tableFailures = await db.BackupTables
            .Where(x => x.BackupId == backupId && x.Status != BackupTableStatus.Succeeded && x.Status != BackupTableStatus.Skipped)
            .OrderBy(x => x.Database)
            .ThenBy(x => x.Table)
            .Select(x => new { x.Database, x.Table, x.Error })
            .ToListAsync(cancellationToken);
        var shardFailures = await db.BackupTableShards
            .Where(x => x.BackupTable!.BackupId == backupId && x.Status == BackupTableStatus.Failed)
            .OrderBy(x => x.BackupTable!.Database)
            .ThenBy(x => x.BackupTable!.Table)
            .ThenBy(x => x.SourceShardNumber)
            .Select(x => new { x.BackupTable!.Database, x.BackupTable.Table, x.SourceShardNumber, x.Host, x.Port, x.Error })
            .ToListAsync(cancellationToken);

        if (shardFailures.Count > 0)
        {
            return string.Join("; ", shardFailures.Select(x => $"Backup failed for {x.Database}.{x.Table} shard {x.SourceShardNumber} on {x.Host}:{x.Port}: {NormalizeFailure(x.Error)}"));
        }

        if (tableFailures.Count > 0)
        {
            return string.Join("; ", tableFailures.Select(x => $"Backup failed for {x.Database}.{x.Table}: {NormalizeFailure(x.Error)}"));
        }

        return "One or more backup tables or shards failed.";
    }

    private static async Task PollBackupShardAsync(IClickHouseAdapter adapter, ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTableShardEntity shard, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!current.Exists)
            {
                throw MissingOperationException(shard.ClickHouseOperationId);
            }

            shard.ClickHouseStatus = current.Status;
            if (IsSuccessStatus(current.Status))
            {
                shard.Status = BackupTableStatus.Succeeded;
                shard.CompletedAt = DateTimeOffset.UtcNow;
                return;
            }
            if (IsFailedStatus(current.Status))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(current.Error) ? $"ClickHouse operation failed with status {current.Status}." : current.Error);
            }

            await Task.Delay(interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval, cancellationToken);
            current = await adapter.GetOperationStatusAsync(endpoint, cluster, shard.ClickHouseOperationId!, cancellationToken);
        }
    }

    private static async Task PollBackupAsync(IClickHouseAdapter adapter, ClickHouseClusterEntity cluster, BackupTableEntity table, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!current.Exists)
            {
                throw MissingOperationException(table.ClickHouseOperationId);
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
        if (backup.BackupType == BackupType.Incremental && backup.PolicyId is null)
        {
            throw new InvalidOperationException("Incremental backups require a policy.");
        }
        if (backup.SourceCluster is null || backup.Target is null)
        {
            throw new InvalidOperationException("Backup source cluster or target was not found.");
        }
    }

    private int EffectiveMaxDop(ClickHouseClusterEntity cluster) =>
        Math.Max(1, cluster.BackupRestoreMaxDop is > 0 ? cluster.BackupRestoreMaxDop.Value : options.Value.MaxDop <= 0 ? 3 : options.Value.MaxDop);

    private static bool IsMergeTreeDataEngine(string engine) =>
        engine.Contains("MergeTree", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedSystemDatabase(string database) =>
        string.Equals(database, "system", StringComparison.Ordinal) ||
        string.Equals(database, "information_schema", StringComparison.Ordinal) ||
        string.Equals(database, "INFORMATION_SCHEMA", StringComparison.Ordinal);

    private async Task<BackupTableEntity?> FindParentFullTableAsync(Guid policyId, string database, string table, CancellationToken cancellationToken) =>
        await db.BackupTables
            .Include(x => x.Backup)
            .Include(x => x.Shards)
            .Where(x => x.Backup != null &&
                        x.Backup.PolicyId == policyId &&
                        x.Backup.Status == BackupRunStatus.Succeeded &&
                        x.EffectiveBackupType == BackupType.Full &&
                        x.Database == database &&
                        x.Table == table)
            .OrderByDescending(x => x.Backup!.CompletedAt ?? x.Backup.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> GetParentTablePathAsync(Guid? parentFullBackupTableId, CancellationToken cancellationToken) =>
        parentFullBackupTableId is null
            ? null
            : await db.BackupTables
                .Where(x => x.Id == parentFullBackupTableId)
                .Select(x => x.S3Path)
                .FirstOrDefaultAsync(cancellationToken);

    private static async Task<string?> GetParentShardPathAsync(ChoboDbContext db, Guid? parentFullBackupTableShardId, CancellationToken cancellationToken) =>
        parentFullBackupTableShardId is null
            ? null
            : await db.BackupTableShards
                .Where(x => x.Id == parentFullBackupTableShardId)
                .Select(x => x.S3Path)
                .FirstOrDefaultAsync(cancellationToken);

    private static string BuildS3Path(BackupEntity backup, string database, string table, BackupType effectiveType, Guid? parentFullBackupId)
    {
        var source = backup.PolicyId is { } policyId ? $"policy-{policyId:N}" : "manual";
        var timestamp = backup.CreatedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        if (effectiveType == BackupType.Incremental)
        {
            var parent = parentFullBackupId is null ? "parent-full-unknown" : $"parent-full-{parentFullBackupId.Value:N}";
            return $"backups/incremental/{source}/{EscapePathPart(database)}/{EscapePathPart(table)}/{parent}/{timestamp}/{backup.Id:N}";
        }

        return $"backups/full/{source}/{EscapePathPart(database)}/{EscapePathPart(table)}/{timestamp}/{backup.Id:N}";
    }

    private static string BuildShardS3Path(string tablePath, int shardNumber) =>
        $"{tablePath}/shards/shard-{shardNumber:0000}";

    private static IReadOnlyList<ClickHouseShardReplicaInfo> SelectShardRepresentatives(IReadOnlyList<ClickHouseShardReplicaInfo> topology) =>
        topology
            .GroupBy(x => x.ShardNumber)
            .OrderBy(x => x.Key)
            .Select(group => group
                .OrderBy(x => x.ErrorsCount)
                .ThenBy(x => x.ReplicaNumber)
                .ThenBy(x => x.Host, StringComparer.Ordinal)
                .ThenBy(x => x.Port)
                .First())
            .ToList();

    private static BackupRunStatus AggregateBackupStatus(IReadOnlyList<BackupTableStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return BackupRunStatus.Succeeded;
        }
        if (statuses.All(x => x == BackupTableStatus.Succeeded || x == BackupTableStatus.Skipped))
        {
            return BackupRunStatus.Succeeded;
        }
        if (statuses.Any(x => x == BackupTableStatus.Succeeded || x == BackupTableStatus.PartiallySucceeded || x == BackupTableStatus.Skipped))
        {
            return BackupRunStatus.PartiallySucceeded;
        }

        return BackupRunStatus.Failed;
    }

    private static BackupTableStatus AggregateBackupTableStatus(IReadOnlyList<BackupTableStatus> statuses)
    {
        if (statuses.Count == 0 || statuses.All(x => x == BackupTableStatus.Succeeded || x == BackupTableStatus.Skipped))
        {
            return BackupTableStatus.Succeeded;
        }
        if (statuses.Any(x => x == BackupTableStatus.Succeeded || x == BackupTableStatus.PartiallySucceeded || x == BackupTableStatus.Skipped))
        {
            return BackupTableStatus.PartiallySucceeded;
        }

        return BackupTableStatus.Failed;
    }

    private static string EscapePathPart(string value) =>
        Uri.EscapeDataString(value).Replace("%2F", "_", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessStatus(string? status) =>
        string.Equals(status, "BACKUP_CREATED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "RESTORED", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedStatus(string? status) =>
        status?.Contains("FAILED", StringComparison.OrdinalIgnoreCase) == true;

    private static InvalidOperationException MissingOperationException(string? operationId) =>
        new($"ClickHouse operation {operationId ?? "(unknown)"} is missing from system.backups; its outcome is unknown.");

    private static string BuildBackupShardFailureReason(IReadOnlyList<BackupTableShardEntity> failedShards) =>
        failedShards.Count == 0
            ? "One or more shards failed."
            : string.Join("; ", failedShards
                .OrderBy(x => x.SourceShardNumber)
                .Select(x => $"Shard {x.SourceShardNumber} on {x.Host}:{x.Port}: {NormalizeFailure(x.Error)}"));

    private static string NormalizeFailure(string? failure) =>
        string.IsNullOrWhiteSpace(failure) ? "No detailed failure was reported." : failure.Trim();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
