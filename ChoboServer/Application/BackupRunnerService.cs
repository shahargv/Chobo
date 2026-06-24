using System.Collections.Concurrent;
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
    BackupRestoreQueueApplicationService queue,
    IBackupStorageManifestService manifests,
    IAuditService audit,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly ConcurrentDictionary<Guid, byte> ActiveBackupRuns = new();
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
        if (!ActiveBackupRuns.TryAdd(backup.Id, 0))
        {
            _logger.Information("Backup run {BackupId} is already active in this process; duplicate execution request skipped.", backup.Id);
            return;
        }

        try
        {
            _logger.Information("Starting backup run {BackupId}. Current status: {Status}.", backup.Id, backup.Status);
            if (!await TryClaimBackupAsync(backup, cancellationToken))
            {
                return;
            }

            await audit.RecordAsync("started", AuditEntityType.Backup, backup.Id.ToString(), new { backup.SourceClusterId, backup.TargetId });

            ValidateBackup(backup);
            if (backup.Tables.Count == 0)
            {
                _logger.Information("Preparing backup tables for backup {BackupId}.", backup.Id);
                await PrepareTablesAsync(backup, cancellationToken);
            }
            await queue.EnsureBackupQueueItemsAsync(backup.Id, cancellationToken);
            await queue.ResetIncompleteBackupClaimsAsync(backup.Id, cancellationToken);

            if (backup.ContentMode == BackupContentMode.SchemaOnly)
            {
                var skipped = await CompleteSchemaOnlyTablesAsync(backup.Id, cancellationToken);
                _logger.Information("Backup {BackupId} is schema-only; marked {TableCount} table(s) complete without data backup execution.", backup.Id, skipped);
                await audit.RecordAsync("schema-only-tables-skipped", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, tableCount = skipped, reason = "schema-only" });
            }
            else
            {
                var maxDop = EffectiveMaxDop(backup.SourceCluster!);
                _logger.Information("Executing backup {BackupId} with effective maxdop {MaxDop} across shard work for {TableCount} table(s).", backup.Id, maxDop, backup.Tables.Count);
                await RunBackupShardWorkAsync(backup.Id, maxDop, cancellationToken);
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
            if (backup.ContentMode == BackupContentMode.SchemaAndData)
            {
                if (backup.Status == BackupRunStatus.Succeeded)
                {
                    await TryWriteFinalManifestAsync(backup, tableCount, cancellationToken);
                }
                else
                {
                    await TryWriteFailedManifestAsync(backup.Id);
                }
            }
            LogBackupCompletion(backup);
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
            if (backup.ContentMode == BackupContentMode.SchemaAndData)
            {
                await TryWriteFailedManifestAsync(backup.Id);
            }
            await audit.RecordAsync("failed", AuditEntityType.Backup, backup.Id.ToString(), new { error = ex.Message, backup.FailureReason });
        }
        finally
        {
            ActiveBackupRuns.TryRemove(backup.Id, out _);
        }
    }


    private async Task<bool> TryClaimBackupAsync(BackupEntity backup, CancellationToken cancellationToken)
    {
        if (backup.Status == BackupRunStatus.Running)
        {
            return true;
        }

        if (backup.TriggerType == BackupTriggerType.Scheduled && backup.PolicyId is { } policyId)
        {
            var now = DateTimeOffset.UtcNow;
            var claimed = await db.Backups
                .Where(x => x.Id == backup.Id &&
                            x.Status == BackupRunStatus.Queued &&
                            !db.Backups.Any(other =>
                                other.Id != backup.Id &&
                                other.PolicyId == policyId &&
                                (other.Status == BackupRunStatus.Running ||
                                 (other.Status == BackupRunStatus.Queued && other.CreatedAt < backup.CreatedAt))))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, BackupRunStatus.Running)
                    .SetProperty(x => x.StartedAt, now), cancellationToken);

            if (claimed == 1)
            {
                backup.Status = BackupRunStatus.Running;
                backup.StartedAt ??= now;
                return true;
            }

            var currentStatus = await db.Backups
                .Where(x => x.Id == backup.Id)
                .Select(x => x.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (currentStatus != BackupRunStatus.Queued)
            {
                return false;
            }

            backup.Status = BackupRunStatus.Canceled;
            backup.CompletedAt = now;
            backup.FailureReason = "Scheduled backup skipped because another backup for the same policy is already queued or running.";
            backup.Error = backup.FailureReason;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Skipped scheduled backup {BackupId} because policy {PolicyId} already has an active backup.", backup.Id, policyId);
            await audit.RecordAsync("scheduled-duplicate-skipped", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, policyId, reason = "active-policy-backup-exists" });
            return false;
        }

        backup.Status = BackupRunStatus.Running;
        backup.StartedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void LogBackupCompletion(BackupEntity backup)
    {
        if (backup.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded)
        {
            _logger.Warning("Backup {BackupId} finished with status {Status}. Failure reason: {FailureReason}.", backup.Id, backup.Status, backup.FailureReason);
            return;
        }

        _logger.Information("Backup {BackupId} finished with status {Status}. Failure reason: {FailureReason}.", backup.Id, backup.Status, backup.FailureReason);
    }
    private Task TryWriteFinalManifestAsync(BackupEntity backup, int tableCount, CancellationToken cancellationToken) =>
        TryWriteManifestAsync(
            backup.Id,
            "final",
            "metadata-manifest-written",
            "metadata-manifest-write-failed",
            "Final backup storage manifest write failed for backup {BackupId}; backup result remains {Status}.",
            new { tableCount },
            new object?[] { backup.Status },
            cancellationToken);

    private Task TryWriteFailedManifestAsync(Guid backupId) =>
        TryWriteManifestAsync(
            backupId,
            "failed-backup",
            null,
            "metadata-manifest-write-failed",
            "Failed-backup storage manifest write failed for backup {BackupId}.",
            null,
            [],
            CancellationToken.None);

    private Task TryWriteCheckpointManifestAsync(Guid backupId, int completedShardAttempts, int totalShardAttempts, CancellationToken cancellationToken) =>
        TryWriteManifestAsync(
            backupId,
            "checkpoint",
            "metadata-manifest-checkpoint-written",
            "metadata-manifest-checkpoint-write-failed",
            "Checkpoint backup storage manifest write failed for backup {BackupId} after {CompletedShardAttempts}/{TotalShardAttempts} shard attempt(s).",
            new { completedShardAttempts, totalShardAttempts },
            new object?[] { completedShardAttempts, totalShardAttempts },
            cancellationToken);

    private async Task TryWriteManifestAsync(Guid backupId, string purpose, string? successAction, string failureAction, string failureMessageTemplate, object? successDetails, object?[] failureMessageArgs, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Value.ManifestWriteTimeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(options.Value.ManifestWriteTimeout);
        }

        try
        {
            await manifests.WriteManifestAsync(backupId, timeout.Token);
            if (successAction is not null)
            {
                await audit.RecordAsync(successAction, AuditEntityType.Backup, backupId.ToString(), successDetails ?? new { purpose });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested || timeout.IsCancellationRequested)
        {
            _logger.Warning(ex, failureMessageTemplate, new object?[] { backupId }.Concat(failureMessageArgs).ToArray());
            await audit.RecordAsync(failureAction, AuditEntityType.Backup, backupId.ToString(), new { purpose, error = ex.Message });
        }
    }

    private async Task<int> CompleteSchemaOnlyTablesAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.BackupTables
            .Where(x => x.BackupId == backupId && x.Status != BackupTableStatus.Succeeded)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, BackupTableStatus.Succeeded)
                .SetProperty(x => x.ClickHouseStatus, "SCHEMA_ONLY")
                .SetProperty(x => x.CompletedAt, now), cancellationToken);
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
        var schemaOnly = backup.ContentMode == BackupContentMode.SchemaOnly || (manualRequest?.SchemaOnly ?? false);
        if (schemaOnly && backup.ContentMode != BackupContentMode.SchemaOnly)
        {
            backup.ContentMode = BackupContentMode.SchemaOnly;
            backup.BackupType = BackupType.Full;
            backup.TargetId = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        var topology = await clickHouse.GetTopologyAsync(backup.SourceCluster!, cancellationToken);
        var representatives = SelectShardRepresentatives(topology);
        var inventory = schemaOnly
            ? await ReadClusterSchemaInventoryAsync(backup, topology, cancellationToken)
            : await clickHouse.GetTablesAsync(backup.SourceCluster!, cancellationToken);
        _logger.Information("Backup {BackupId} inventory contains {InventoryCount} table(s).", backup.Id, inventory.Count);
        var selectableInventory = inventory
            .Where(x => !IsExcludedSystemDatabase(x.Database))
            .ToList();
        var selected = selectorEvaluation.Evaluate(selector, new PolicyInventory(selectableInventory.Select(x => new PolicyInventoryTable(x.Database, x.Table)).ToList()));
        _logger.Information("Backup {BackupId} selector matched {SelectedCount} table(s).", backup.Id, selected.Count);
        var selectedSet = selected.Select(x => ClickHouseBackupIdentity.Table(x.Database, x.Table)).ToHashSet(StringComparer.Ordinal);
        var selectedTables = selectableInventory
            .Where(x => selectedSet.Contains(ClickHouseBackupIdentity.Table(x.Database, x.Table)))
            .ToList();
        var selectedSchemaHashes = selectedTables
            .Select(x => x.SchemaHash)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var schemasByHash = await db.SchemaDefinitions
            .Where(x => selectedSchemaHashes.Contains(x.SchemaHash))
            .ToDictionaryAsync(x => x.SchemaHash, StringComparer.Ordinal, cancellationToken);
        var parentTablesByIdentity = backup.BackupType == BackupType.Incremental && backup.PolicyId is not null
            ? await FindParentFullTablesAsync(backup.PolicyId.Value, selectedTables, cancellationToken)
            : [];

        foreach (var table in selectedTables)
        {
            if (!schemasByHash.TryGetValue(table.SchemaHash, out var schema))
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
                schemasByHash[table.SchemaHash] = schema;
                db.SchemaDefinitions.Add(schema);
                _logger.Debug("Prepared new schema definition {SchemaDefinitionId} for {Database}.{Table} hash {SchemaHash}.", schema.Id, table.Database, table.Table, table.SchemaHash);
            }

            var dataBackedUp = !schemaOnly && IsMergeTreeDataEngine(table.Engine);
            var parentTable = backup.BackupType == BackupType.Incremental && dataBackedUp
                ? parentTablesByIdentity.GetValueOrDefault(ClickHouseBackupIdentity.Table(table.Database, table.Table))
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
            if (!schemaOnly)
            {
                _logger.Information("Prepared backup table {Database}.{Table} engine {Engine} dataBackedUp={DataBackedUp}.", table.Database, table.Table, table.Engine, dataBackedUp);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(backup).Collection(x => x.Tables).LoadAsync(cancellationToken);
        _logger.Information("Backup {BackupId} prepared {TableCount} table row(s).", backup.Id, backup.Tables.Count);
        var shardCount = await db.BackupTableShards.CountAsync(x => x.BackupTable!.BackupId == backup.Id, cancellationToken);
        await audit.RecordAsync("tables-prepared", AuditEntityType.Backup, backup.Id.ToString(), new { tableCount = backup.Tables.Count, shardCount });
        await audit.RecordAsync("shards-prepared", AuditEntityType.Backup, backup.Id.ToString(), new { shardCount });
    }

    private sealed record BackupShardWorkItem(Guid TableId, Guid ShardId);

    private static bool IsCancellationTerminalStatus(BackupRunStatus status) =>
        status is BackupRunStatus.Canceled or
            BackupRunStatus.ManualDeleteRequested or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeleteRequested or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleteStarted or
            BackupRunStatus.BackupExpiredDeleted;

    private static bool IsShardTerminalStatus(BackupTableStatus status) =>
        status is BackupTableStatus.Succeeded or BackupTableStatus.Failed or BackupTableStatus.Skipped;

    private async Task<bool> IsBackupCancellationTerminalAsync(ChoboDbContext context, Guid backupId, CancellationToken cancellationToken)
    {
        var status = await context.Backups
            .Where(x => x.Id == backupId)
            .Select(x => x.Status)
            .FirstAsync(cancellationToken);
        return IsCancellationTerminalStatus(status);
    }

    private async Task RunBackupShardWorkAsync(Guid backupId, int maxDop, CancellationToken cancellationToken)
    {
        var workItems = await PrepareBackupTablesForShardWorkAsync(backupId, cancellationToken);
        if (workItems.Count > 0)
        {
            var completedShardAttempts = 0;
            var checkpointInterval = options.Value.ManifestCheckpointShardInterval;
            var forcedWorkCount = await CountForcedBackupWorkAsync(backupId, cancellationToken);
            var workerCount = Math.Min(workItems.Count, Math.Max(1, maxDop) + forcedWorkCount);
            var workers = Enumerable.Range(0, workerCount)
                .Select(async _ =>
                {
                    while (true)
                    {
                        BackupRestoreQueueApplicationService.QueueClaimResult claim;
                        using (var statusScope = scopeFactory.CreateScope())
                        {
                            var statusDb = statusScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
                            if (await IsBackupCancellationTerminalAsync(statusDb, backupId, cancellationToken))
                            {
                                _logger.Information("Backup {BackupId} shard worker observed cancellation/delete-pending status and stopped taking new shard work.", backupId);
                                return;
                            }
                            var queue = statusScope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
                            claim = await queue.TryTakeNextBackupWorkAsync(backupId, cancellationToken);
                        }
                        if (claim.WorkItem is null)
                        {
                            if (!claim.HasQueuedWork)
                            {
                                return;
                            }
                            await Task.Delay(options.Value.PollInterval, cancellationToken);
                            continue;
                        }

                        var item = claim.WorkItem;
                        await RunShardAsync(backupId, item.TableId, item.ShardId, item.IsForced, cancellationToken);                        var completed = Interlocked.Increment(ref completedShardAttempts);
                        using (var statusScope = scopeFactory.CreateScope())
                        {
                            var statusDb = statusScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
                            if (await IsBackupCancellationTerminalAsync(statusDb, backupId, cancellationToken))
                            {
                                _logger.Information("Backup {BackupId} shard worker observed cancellation/delete-pending status after shard attempt and skipped checkpoint manifest.", backupId);
                                return;
                            }
                        }
                        if (checkpointInterval > 0 && completed % checkpointInterval == 0)
                        {
                            await TryWriteCheckpointManifestAsync(backupId, completed, workItems.Count, cancellationToken);
                        }
                    }
                })
                .ToList();

            await Task.WhenAll(workers);
        }

        using var statusScope = scopeFactory.CreateScope();
        var statusDb = statusScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        if (await IsBackupCancellationTerminalAsync(statusDb, backupId, cancellationToken))
        {
            _logger.Information("Backup {BackupId} observed cancellation/delete-pending status before table finalization.", backupId);
            return;
        }

        await FinalizeBackupTablesAsync(backupId, cancellationToken);
    }

    private async Task<int> CountForcedBackupWorkAsync(Guid backupId, CancellationToken cancellationToken) =>
        await db.BackupRestoreQueueItems.AsNoTracking()
            .CountAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backupId && x.IsForced && x.StartedAt == null && x.CompletedAt == null, cancellationToken);
    private async Task<IReadOnlyList<BackupShardWorkItem>> PrepareBackupTablesForShardWorkAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var backup = await db.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstAsync(x => x.Id == backupId, cancellationToken);

        if (IsCancellationTerminalStatus(backup.Status))
        {
            _logger.Information("Backup {BackupId} is {Status}; skipping shard work preparation.", backup.Id, backup.Status);
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var table in backup.Tables.Where(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running).OrderBy(x => x.Database).ThenBy(x => x.Table).ToList())
        {
            if (!table.DataBackedUp)
            {
                _logger.Information("Backup table {BackupTableId} {Database}.{Table} marked schema-only; skipping data backup.", table.Id, table.Database, table.Table);
                table.Status = BackupTableStatus.Succeeded;
                table.ClickHouseStatus = "SCHEMA_ONLY";
                table.CompletedAt = now;
                await audit.RecordAsync("table-skipped", AuditEntityType.BackupTable, table.Id.ToString(), new { reason = "schema-only", table.Database, table.Table });
                continue;
            }

            if (table.Shards.Count == 0)
            {
                var node = backup.SourceCluster!.AccessNodes[0];
                table.Shards.Add(new BackupTableShardEntity
                {
                    SourceShardNumber = 1,
                    SourceShardName = "single",
                    ReplicaNumber = 1,
                    Host = node.Host,
                    Port = node.Port,
                    UseTls = node.UseTls,
                    S3Path = table.S3Path
                });
            }

            table.Status = BackupTableStatus.Running;
            table.StartedAt ??= now;
            _logger.Information("Backup table {BackupTableId} {Database}.{Table} started.", table.Id, table.Database, table.Table);
            await audit.RecordAsync("table-started", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table });
        }

        await db.SaveChangesAsync(cancellationToken);

        var candidateShardIds = backup.Tables
            .Where(x => x.DataBackedUp && (x.Status == BackupTableStatus.Queued || x.Status == BackupTableStatus.Running))
            .SelectMany(table => table.Shards
                .Where(shard => shard.Status is BackupTableStatus.Queued or BackupTableStatus.Running)
                .Select(shard => shard.Id))
            .ToHashSet();
        var queueOrder = await db.BackupRestoreQueueItems
            .AsNoTracking()
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backupId && candidateShardIds.Contains(x.ShardId))
            .OrderByDescending(x => x.IsForced)
            .ThenBy(x => x.Position)
            .Select(x => new BackupShardWorkItem(x.TableId, x.ShardId))
            .ToListAsync(cancellationToken);
        return queueOrder.Count > 0
            ? queueOrder
            : backup.Tables
                .Where(x => x.DataBackedUp && (x.Status == BackupTableStatus.Queued || x.Status == BackupTableStatus.Running))
                .OrderBy(x => x.Database)
                .ThenBy(x => x.Table)
                .SelectMany(table => table.Shards
                    .Where(shard => shard.Status is BackupTableStatus.Queued or BackupTableStatus.Running)
                    .OrderBy(shard => shard.SourceShardNumber)
                    .Select(shard => new BackupShardWorkItem(table.Id, shard.Id)))
                .ToList();
    }

    private async Task<bool> ReloadShardAndStopIfCanceledAsync(ChoboDbContext context, BackupEntity backup, BackupTableEntity table, BackupTableShardEntity shard, CancellationToken cancellationToken)
    {
        await context.Entry(backup).ReloadAsync(cancellationToken);
        await context.Entry(table).ReloadAsync(cancellationToken);
        await context.Entry(shard).ReloadAsync(cancellationToken);
        if (IsCancellationTerminalStatus(backup.Status) || shard.Status is BackupTableStatus.Skipped or BackupTableStatus.Failed)
        {
            _logger.Information("Backup shard {BackupShardId} stopped before status write because backup {BackupId} is {BackupStatus} and shard status is {ShardStatus}.", shard.Id, backup.Id, backup.Status, shard.Status);
            return true;
        }

        return false;
    }

    private async Task RunShardAsync(Guid backupId, Guid tableId, Guid shardId, bool isForcedQueueItem, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var scopedOptions = scope.ServiceProvider.GetRequiredService<IOptions<ChoboBackupRestoreOptions>>();
        var scopedQueue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
        var scopedTestHooks = scope.ServiceProvider.GetRequiredService<ITestHookCoordinator>();
        var scopedStorage = scope.ServiceProvider.GetRequiredService<IBackupStorageOperations>();

        var backup = await scopedDb.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Target)
            .Include(x => x.Tables.Where(table => table.Id == tableId)).ThenInclude(x => x.Shards.Where(shard => shard.Id == shardId))
            .FirstAsync(x => x.Id == backupId, cancellationToken);
        var table = backup.Tables.Single();
        var shard = table.Shards.Single();

        if (IsCancellationTerminalStatus(backup.Status) || IsShardTerminalStatus(shard.Status))
        {
            _logger.Information("Backup shard {BackupShardId} skipped because backup {BackupId} is {BackupStatus} and shard status is {ShardStatus}.", shard.Id, backup.Id, backup.Status, shard.Status);
            return;
        }

        shard.Status = BackupTableStatus.Running;
        shard.StartedAt ??= DateTimeOffset.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);
        await scopedAudit.RecordAsync("shard-started", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, sourceNode = $"{shard.Host}:{shard.Port}" });

        try
        {
            var endpoint = new ClickHouseNodeEndpoint(shard.Host, shard.Port, shard.UseTls);
            if (string.IsNullOrWhiteSpace(shard.ClickHouseOperationId) && IsReplicatedMergeTreeEngine(table.Engine))
            {
                var candidates = (await scopedClickHouse.GetTopologyAsync(backup.SourceCluster!, cancellationToken))
                    .Where(x => x.ShardNumber == shard.SourceShardNumber)
                    .OrderBy(_ => Random.Shared.Next())
                    .ToList();
                var selectedCandidateIndex = -1;
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (!await scopedQueue.TryReserveStartedNodeAsync(BackupRestoreQueueKind.Backup, shard.Id, backup.SourceClusterId, candidates[i].Endpoint, isForcedQueueItem, cancellationToken))
                    {
                        continue;
                    }
                    selectedCandidateIndex = i;
                    break;
                }
                if (selectedCandidateIndex < 0 && candidates.Count > 0)
                {
                    shard.Status = BackupTableStatus.Queued;
                    shard.StartedAt = null;
                    await scopedDb.SaveChangesAsync(cancellationToken);
                    await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                    return;
                }
                if (selectedCandidateIndex >= 0)
                {
                    var selectedCandidate = candidates[selectedCandidateIndex];
                    endpoint = selectedCandidate.Endpoint;
                    shard.SourceShardName = selectedCandidate.ShardName;
                    shard.ReplicaNumber = selectedCandidate.ReplicaNumber;
                    shard.Host = selectedCandidate.Host;
                    shard.Port = selectedCandidate.Port;
                    shard.UseTls = selectedCandidate.UseTls;
                    await scopedDb.SaveChangesAsync(cancellationToken);
                }
            }
            await scopedQueue.MarkStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, endpoint, cancellationToken);
            if (!string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
            {
                var status = await scopedClickHouse.GetOperationStatusAsync(endpoint, backup.SourceCluster!, shard.ClickHouseOperationId, cancellationToken);
                if (status.Exists)
                {
                    var resumedFinalClickHouseStatus = await PollBackupShardAsync(scopedDb, scopedClickHouse, endpoint, backup.SourceCluster!, backup.Id, shard, status, scopedOptions.Value.PollInterval, cancellationToken);
                    if (resumedFinalClickHouseStatus is null || await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
                    {
                        return;
                    }
                    var resumedBackupSizeBytes = await MeasureBackupPathAsync(scopedStorage, backup.Target!, shard.S3Path, cancellationToken);
                    if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
                    {
                        return;
                    }
                    shard.Status = BackupTableStatus.Succeeded;
                    shard.ClickHouseStatus = resumedFinalClickHouseStatus;
                    shard.CompletedAt = DateTimeOffset.UtcNow;
                    shard.BackupSizeBytes = resumedBackupSizeBytes;
                    await scopedDb.SaveChangesAsync(cancellationToken);
                    await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, shard.ClickHouseStatus, shard.BackupSizeBytes });
                    await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                    return;
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
            var finalClickHouseStatus = await PollBackupShardAsync(scopedDb, scopedClickHouse, endpoint, backup.SourceCluster!, backup.Id, shard, new ClickHouseOperationStatus(true, operation.Status, null), scopedOptions.Value.PollInterval, cancellationToken);
            if (finalClickHouseStatus is null || await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
            {
                return;
            }
            var backupSizeBytes = await MeasureBackupPathAsync(scopedStorage, backup.Target!, shard.S3Path, cancellationToken);
            if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
            {
                return;
            }
            shard.Status = BackupTableStatus.Succeeded;
            shard.ClickHouseStatus = finalClickHouseStatus;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            shard.BackupSizeBytes = backupSizeBytes;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, shard.ClickHouseStatus, shard.BackupSizeBytes });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, CancellationToken.None))
            {
                return;
            }

            _logger.Error(ex, "Backup table {BackupTableId} {Database}.{Table} shard {ShardNumber} failed.", table.Id, table.Database, table.Table, shard.SourceShardNumber);
            shard.Status = BackupTableStatus.Failed;
            shard.Error = ex.Message;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("shard-failed", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = ex.Message, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, CancellationToken.None);
        }
    }

    private async Task FinalizeBackupTablesAsync(Guid backupId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var tables = await scopedDb.BackupTables
            .Include(x => x.Shards)
            .Where(x => x.BackupId == backupId && x.DataBackedUp && (x.Status == BackupTableStatus.Queued || x.Status == BackupTableStatus.Running))
            .OrderBy(x => x.Database)
            .ThenBy(x => x.Table)
            .ToListAsync(cancellationToken);

        foreach (var table in tables)
        {
            table.Status = AggregateBackupTableStatus(table.Shards.Select(x => x.Status).ToList());
            table.ClickHouseStatus = table.Shards.Any(x => x.Status == BackupTableStatus.Failed) ? "PARTIAL_OR_FAILED" : table.Shards.LastOrDefault()?.ClickHouseStatus;
            table.Error = table.Status is BackupTableStatus.Failed or BackupTableStatus.PartiallySucceeded
                ? BuildBackupShardFailureReason(table.Shards.Where(x => x.Status == BackupTableStatus.Failed).ToList())
                : null;
            table.BackupSizeBytes = table.Shards.Any(x => x.BackupSizeBytes.HasValue) ? table.Shards.Sum(x => x.BackupSizeBytes ?? 0) : null;
            table.CompletedAt = DateTimeOffset.UtcNow;
            var tableAction = table.Status == BackupTableStatus.Succeeded ? "table-succeeded" : table.Status == BackupTableStatus.PartiallySucceeded ? "table-partially-succeeded" : "table-failed";
            await scopedAudit.RecordAsync(tableAction, AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, shardCount = table.Shards.Count, clickHouseOperationId = table.ClickHouseOperationId, failureReason = table.Error, table.BackupSizeBytes });
        }

        await scopedDb.SaveChangesAsync(cancellationToken);
    }


    private static async Task<long> MeasureBackupPathAsync(IBackupStorageOperations storage, BackupTargetEntity target, string path, CancellationToken cancellationToken)
    {
        var objects = await storage.ListObjectsAsync(target, path, cancellationToken);
        return objects
            .Where(x => !x.Path.EndsWith(BackupStorageManifestService.ManifestRelativePath, StringComparison.Ordinal))
            .Sum(x => x.SizeBytes);
    }

    private async Task<IReadOnlyList<ClickHouseTableInfo>> ReadClusterSchemaInventoryAsync(BackupEntity backup, IReadOnlyList<ClickHouseShardReplicaInfo> topology, CancellationToken cancellationToken)
    {
        var orderedNodes = topology
            .OrderBy(x => x.ShardNumber)
            .ThenBy(x => x.ReplicaNumber)
            .ThenBy(x => x.Host, StringComparer.Ordinal)
            .ThenBy(x => x.Port)
            .ToList();
        if (orderedNodes.Count == 0)
        {
            return await clickHouse.GetTablesAsync(backup.SourceCluster!, cancellationToken);
        }

        var byName = new Dictionary<string, ClickHouseTableInfo>(StringComparer.Ordinal);
        var duplicateCount = 0;
        foreach (var node in orderedNodes)
        {
            var tables = await clickHouse.GetTablesAsync(node.Endpoint, backup.SourceCluster!, cancellationToken);
            foreach (var table in tables)
            {
                var key = ClickHouseBackupIdentity.Table(table.Database, table.Table);
                if (byName.ContainsKey(key))
                {
                    duplicateCount++;
                    continue;
                }

                byName[key] = table;
            }
        }

        if (duplicateCount > 0)
        {
            await audit.RecordAsync("schema-inventory-deduplicated", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, duplicateCount, nodeCount = orderedNodes.Count });
        }

        return byName.Values
            .OrderBy(x => x.Database, StringComparer.Ordinal)
            .ThenBy(x => x.Table, StringComparer.Ordinal)
            .ToList();
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

    private async Task<string?> PollBackupShardAsync(ChoboDbContext context, IClickHouseAdapter adapter, ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, Guid backupId, BackupTableShardEntity shard, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await IsBackupCancellationTerminalAsync(context, backupId, cancellationToken))
            {
                _logger.Information("Backup shard {BackupShardId} stopped polling because backup {BackupId} is canceled/delete-pending.", shard.Id, backupId);
                return null;
            }
            if (!current.Exists)
            {
                throw MissingOperationException(shard.ClickHouseOperationId);
            }

            shard.ClickHouseStatus = current.Status;
            if (IsSuccessStatus(current.Status))
            {
                return current.Status;
            }
            if (IsFailedStatus(current.Status))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(current.Error) ? $"ClickHouse operation failed with status {current.Status}." : current.Error);
            }

            await Task.Delay(interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval, cancellationToken);
            if (await IsBackupCancellationTerminalAsync(context, backupId, cancellationToken))
            {
                _logger.Information("Backup shard {BackupShardId} stopped polling after delay because backup {BackupId} is canceled/delete-pending.", shard.Id, backupId);
                return null;
            }
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
        if (backup.SourceCluster is null)
        {
            throw new InvalidOperationException("Backup source cluster was not found.");
        }
        if (backup.ContentMode == BackupContentMode.SchemaAndData && backup.Target is null)
        {
            throw new InvalidOperationException("Backup target was not found.");
        }
        if (backup.ContentMode == BackupContentMode.SchemaOnly && backup.BackupType == BackupType.Incremental)
        {
            throw new InvalidOperationException("Schema-only backups must be full backups.");
        }
    }
    private int EffectiveMaxDop(ClickHouseClusterEntity cluster) =>
        Math.Max(1, cluster.BackupRestoreMaxDop > 0 ? cluster.BackupRestoreMaxDop : options.Value.MaxDop <= 0 ? 3 : options.Value.MaxDop);


    private static bool IsReplicatedMergeTreeEngine(string engine) =>
        engine.Contains("Replicated", StringComparison.OrdinalIgnoreCase) && engine.Contains("MergeTree", StringComparison.OrdinalIgnoreCase);
    private static bool IsMergeTreeDataEngine(string engine) =>
        engine.EndsWith("MergeTree", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedSystemDatabase(string database) =>
        string.Equals(database, "system", StringComparison.Ordinal) ||
        string.Equals(database, "information_schema", StringComparison.Ordinal) ||
        string.Equals(database, "INFORMATION_SCHEMA", StringComparison.Ordinal);

    private async Task<Dictionary<string, BackupTableEntity>> FindParentFullTablesAsync(Guid policyId, IReadOnlyList<ClickHouseTableInfo> selectedTables, CancellationToken cancellationToken)
    {
        var selectedIdentities = selectedTables
            .Select(x => ClickHouseBackupIdentity.Table(x.Database, x.Table))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIdentities.Count == 0)
        {
            return [];
        }

        var databases = selectedTables.Select(x => x.Database).Distinct(StringComparer.Ordinal).ToList();
        var tables = selectedTables.Select(x => x.Table).Distinct(StringComparer.Ordinal).ToList();
        var candidates = await db.BackupTables
            .AsNoTracking()
            .Include(x => x.Backup)
            .Include(x => x.Shards)
            .AsSplitQuery()
            .Where(x => x.Backup != null &&
                        x.Backup.PolicyId == policyId &&
                        x.Backup.Status == BackupRunStatus.Succeeded &&
                        x.EffectiveBackupType == BackupType.Full &&
                        databases.Contains(x.Database) &&
                        tables.Contains(x.Table))
            .OrderByDescending(x => x.Backup!.CompletedAt ?? x.Backup.CreatedAt)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(x => selectedIdentities.Contains(ClickHouseBackupIdentity.Table(x.Database, x.Table)))
            .GroupBy(x => ClickHouseBackupIdentity.Table(x.Database, x.Table), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
    }

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
