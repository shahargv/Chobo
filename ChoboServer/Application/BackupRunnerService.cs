using System.Collections.Concurrent;
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
    IOptionsMonitor<ChoboBackupRestoreOptions> options,
    BackupPreparationService preparation,
    BackupRestoreQueueApplicationService queue,
    IBackupStorageManifestService manifests,
    IAuditService audit,
    Serilog.ILogger logger)
{
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
            if (backup.Status == BackupRunStatus.Running)
            {
                await queue.ResetIncompleteBackupNodeClaimsAsync(backup.Id, cancellationToken);
            }
            if (!await TryClaimBackupAsync(backup, cancellationToken))
            {
                return;
            }

            await audit.RecordAsync("started", AuditEntityType.Backup, backup.Id.ToString(), new { backup.SourceClusterId, backup.TargetId });

            await preparation.PrepareQueueItemsAsync(backup.Id, cancellationToken);

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
            var auditAction = backup.Status == BackupRunStatus.Succeeded ? "succeeded" : backup.Status == BackupRunStatus.PartiallySucceeded ? "partially-succeeded" : "failed";
            if (backup.ContentMode == BackupContentMode.SchemaAndData && backup.Status == BackupRunStatus.Succeeded)
            {
                await TryWriteFinalManifestAsync(backup, tableCount, cancellationToken);
            }
            LogBackupCompletion(backup);
            await audit.RecordAsync(auditAction, AuditEntityType.Backup, backup.Id.ToString(), new { tableCount, backup.FailureReason });
            if (backup.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded)
            {
                await queue.RemoveActiveOperationItemsAsync(BackupRestoreQueueKind.Backup, backup.Id, "backup-finished-unsuccessfully", cancellationToken);
            }
            if (backup.ContentMode == BackupContentMode.SchemaAndData && backup.Status != BackupRunStatus.Succeeded)
            {
                await TryWriteFailedManifestAsync(backup.Id);
            }
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
            await queue.RemoveActiveOperationItemsAsync(BackupRestoreQueueKind.Backup, backup.Id, "backup-runner-exception", CancellationToken.None);
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
            var hasActiveClaim = await db.BackupRestoreQueueItems
                .AsNoTracking()
                .AnyAsync(x => x.Kind == BackupRestoreQueueKind.Backup &&
                               x.OperationId == backup.Id &&
                               x.StartedAt != null &&
                               x.CompletedAt == null, cancellationToken);
            if (hasActiveClaim)
            {
                _logger.Information("Backup run {BackupId} is already active according to queue claims; duplicate execution request skipped.", backup.Id);
                return false;
            }

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
        if (options.CurrentValue.ManifestWriteTimeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(options.CurrentValue.ManifestWriteTimeout);
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
                    await PollBackupAsync(clickHouse, backup.SourceCluster!, table, status, options.CurrentValue.PollInterval, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.Information("Backup table {BackupTableId} {Database}.{Table} completed with ClickHouse status {Status}.", table.Id, table.Database, table.Table, table.ClickHouseStatus);
                    await audit.RecordAsync("table-succeeded", AuditEntityType.BackupTable, table.Id.ToString(), new { table.Database, table.Table, clickHouseOperationId = table.ClickHouseOperationId, table.ClickHouseStatus });
                    return;
                }

                throw MissingOperationException(table.ClickHouseOperationId);
            }

            var baseBackupPath = await GetParentTablePathAsync(table.ParentFullBackupTableId, cancellationToken);
            var settings = ClickHouseAdvancedSettings.Deserialize(backup.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup);
            var operation = await clickHouse.StartBackupAsync(backup.SourceCluster!, backup.Target!, table, baseBackupPath, settings, cancellationToken);
            table.ClickHouseOperationId = operation.OperationId;
            table.ClickHouseStatus = operation.Status;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup table {BackupTableId} submitted ClickHouse operation {OperationId} status {Status}.", table.Id, operation.OperationId, operation.Status);
            await audit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.BackupTable, table.Id.ToString(), new { clickHouseOperationId = operation.OperationId, operation.Status });
            await PollBackupAsync(clickHouse, backup.SourceCluster!, table, new ClickHouseOperationStatus(true, operation.Status, null), options.CurrentValue.PollInterval, cancellationToken);
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

    private sealed record BackupShardWorkItem(Guid TableId, Guid ShardId);

    private enum BackupShardRunResult
    {
        Completed,
        RetryLater
    }

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
            var checkpointInterval = options.CurrentValue.ManifestCheckpointShardInterval;
            var retryCounts = new ConcurrentDictionary<Guid, int>();
            var failedEndpoints = new ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>>();
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
                            await Task.Delay(options.CurrentValue.PollInterval, cancellationToken);
                            continue;
                        }

                        var item = claim.WorkItem;
                        var result = await RunShardAsync(backupId, item.TableId, item.ShardId, item.IsForced, item.IsResume, retryCounts, failedEndpoints, cancellationToken);
                        if (result == BackupShardRunResult.RetryLater)
                        {
                            await Task.Delay(options.CurrentValue.PollInterval, cancellationToken);
                            continue;
                        }
                        var completed = Interlocked.Increment(ref completedShardAttempts);
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
                    StoragePath = table.StoragePath
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

    private async Task<BackupShardRunResult> RunShardAsync(Guid backupId, Guid tableId, Guid shardId, bool isForcedQueueItem, bool isResumeQueueItem, ConcurrentDictionary<Guid, int> retryCounts, ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> failedEndpoints, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var scopedOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ChoboBackupRestoreOptions>>();
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
            await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
            return BackupShardRunResult.Completed;
        }

        shard.Status = BackupTableStatus.Running;
        shard.StartedAt ??= DateTimeOffset.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);
        await scopedAudit.RecordAsync("shard-started", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, sourceNode = $"{shard.Host}:{shard.Port}" });

        var endpoint = new ClickHouseNodeEndpoint(shard.Host, shard.Port, shard.UseTls);
        var nodeReserved = false;
        var backupSubmissionAttempted = false;
        try
        {
            if (!isResumeQueueItem && string.IsNullOrWhiteSpace(shard.ClickHouseOperationId) && IsReplicatedMergeTreeEngine(table.Engine))
            {
                var failedEndpointKeys = failedEndpoints.TryGetValue(shard.Id, out var failed) ? failed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
                var shardReplicas = (await scopedClickHouse.GetTopologyAsync(backup.SourceCluster!, cancellationToken))
                    .Where(x => x.ShardNumber == shard.SourceShardNumber)
                    .ToList();
                var availableReplicas = await GetAvailableBackupReplicaCandidatesAsync(scopedClickHouse, backup.SourceCluster!, shardReplicas, cancellationToken);
                var candidates = availableReplicas
                    .OrderBy(x => failedEndpointKeys.Contains(EndpointKey(x.Endpoint)))
                    .ThenBy(_ => Random.Shared.Next())
                    .ToList();
                var selectedCandidateIndex = -1;
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (!await scopedQueue.TryReserveStartedNodeAsync(BackupRestoreQueueKind.Backup, shard.Id, backup.SourceClusterId, candidates[i].Endpoint, isForcedQueueItem, cancellationToken))
                    {
                        continue;
                    }
                    selectedCandidateIndex = i;
                    nodeReserved = true;
                    break;
                }
                if (selectedCandidateIndex < 0 && candidates.Count > 0)
                {
                    shard.Status = BackupTableStatus.Queued;
                    shard.StartedAt = null;
                    await scopedDb.SaveChangesAsync(cancellationToken);
                    await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                    return BackupShardRunResult.RetryLater;
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
            if (!nodeReserved && !await scopedQueue.TryReserveStartedNodeAsync(BackupRestoreQueueKind.Backup, shard.Id, backup.SourceClusterId, endpoint, isForcedQueueItem, cancellationToken))
            {
                shard.Status = BackupTableStatus.Queued;
                shard.StartedAt = null;
                await scopedDb.SaveChangesAsync(cancellationToken);
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                return BackupShardRunResult.RetryLater;
            }
            await scopedQueue.MarkStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, endpoint, cancellationToken);
            if (isResumeQueueItem && string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
            {
                ClickHouseDiscoveredOperation? discovered;
                var recoveryCheckFailed = false;
                try
                {
                    discovered = await scopedClickHouse.FindLatestBackupOperationForPathAsync(endpoint, backup.SourceCluster!, backup.Target!, shard.StoragePath, cancellationToken);
                }
                catch (Exception recoveryEx) when (recoveryEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.Warning(recoveryEx, "Backup table {BackupTableId} {Database}.{Table} shard {ShardNumber} resumed without a recorded ClickHouse operation id, and operation discovery failed for {StoragePath}; submitting a fresh attempt path.", table.Id, table.Database, table.Table, shard.SourceShardNumber, shard.StoragePath);
                    await scopedAudit.RecordAsync("clickhouse-operation-recovery-check-failed", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = recoveryEx.Message, sourceShard = shard.SourceShardNumber, shard.StoragePath, reason = "missing-operation-id-on-resume" });
                    discovered = null;
                    recoveryCheckFailed = true;
                }

                if (discovered is null && !recoveryCheckFailed)
                {
                    _logger.Warning("Backup table {BackupTableId} {Database}.{Table} shard {ShardNumber} was resumed without a recorded ClickHouse operation id and no matching ClickHouse backup operation was found for {StoragePath}; submitting a fresh attempt path.", table.Id, table.Database, table.Table, shard.SourceShardNumber, shard.StoragePath);
                    await scopedAudit.RecordAsync("clickhouse-operation-recovery-not-found", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { sourceShard = shard.SourceShardNumber, shard.StoragePath, reason = "missing-operation-id-on-resume" });
                }

                if (discovered is not null)
                {
                    shard.ClickHouseOperationId = discovered.OperationId;
                    shard.ClickHouseStatus = discovered.Status;
                    table.ClickHouseOperationId ??= discovered.OperationId;
                    table.ClickHouseStatus = discovered.Status;
                    await scopedDb.SaveChangesAsync(cancellationToken);
                    await scopedAudit.RecordAsync("clickhouse-operation-recovered", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { clickHouseOperationId = discovered.OperationId, discovered.Status, sourceShard = shard.SourceShardNumber, shard.StoragePath, recovered = true, reason = "missing-operation-id-on-resume" });
                }
            }
            if (!string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
            {
                var status = await scopedClickHouse.GetOperationStatusAsync(endpoint, backup.SourceCluster!, shard.ClickHouseOperationId, cancellationToken);
                if (status.Exists)
                {
                    var resumedFinalClickHouseStatus = await PollBackupShardAsync(scopedDb, scopedClickHouse, endpoint, backup.SourceCluster!, backup.Id, shard, status, scopedOptions.CurrentValue.PollInterval, cancellationToken);
                    if (resumedFinalClickHouseStatus is null || await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
                    {
                        await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                        return BackupShardRunResult.Completed;
                    }
                    var resumedBackupSizeBytes = await MeasureBackupPathAsync(scopedStorage, backup.Target!, shard.StoragePath, cancellationToken);
                    if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
                    {
                        await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                        return BackupShardRunResult.Completed;
                    }
                    shard.Status = BackupTableStatus.Succeeded;
                    shard.ClickHouseStatus = resumedFinalClickHouseStatus;
                    shard.Error = null;
                    shard.CompletedAt = DateTimeOffset.UtcNow;
                    shard.BackupSizeBytes = resumedBackupSizeBytes;
                    await scopedDb.SaveChangesAsync(cancellationToken);
                    await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, shard.ClickHouseStatus, shard.BackupSizeBytes });
                    await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                    return BackupShardRunResult.Completed;
                }

                throw MissingOperationException(shard.ClickHouseOperationId);
            }

            await AssignNewShardAttemptStoragePathAsync(scopedDb, scopedAudit, table, shard, cancellationToken);
            var baseBackupPath = await GetParentShardPathAsync(scopedDb, shard.ParentFullBackupTableShardId, cancellationToken);
            var settings = ClickHouseAdvancedSettings.Deserialize(backup.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup);
            backupSubmissionAttempted = true;
            var operation = await scopedClickHouse.StartBackupShardAsync(endpoint, backup.SourceCluster!, backup.Target!, table, shard, baseBackupPath, settings, cancellationToken);
            shard.ClickHouseOperationId = operation.OperationId;
            shard.ClickHouseStatus = operation.Status;
            table.ClickHouseOperationId ??= operation.OperationId;
            table.ClickHouseStatus = operation.Status;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { clickHouseOperationId = operation.OperationId, operation.Status, sourceShard = shard.SourceShardNumber });
            await scopedTestHooks.MaybeDelayBackupBeforePollAsync(cancellationToken);
            var finalClickHouseStatus = await PollBackupShardAsync(scopedDb, scopedClickHouse, endpoint, backup.SourceCluster!, backup.Id, shard, new ClickHouseOperationStatus(true, operation.Status, null), scopedOptions.CurrentValue.PollInterval, cancellationToken);
            if (finalClickHouseStatus is null || await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
            {
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                return BackupShardRunResult.Completed;
            }
            var backupSizeBytes = await MeasureBackupPathAsync(scopedStorage, backup.Target!, shard.StoragePath, cancellationToken);
            if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
            {
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                return BackupShardRunResult.Completed;
            }
            shard.Status = BackupTableStatus.Succeeded;
            shard.ClickHouseStatus = finalClickHouseStatus;
            shard.Error = null;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            shard.BackupSizeBytes = backupSizeBytes;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, shard.ClickHouseStatus, shard.BackupSizeBytes });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
            return BackupShardRunResult.Completed;
        }
        catch (Exception ex)
        {
            if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, CancellationToken.None))
            {
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, CancellationToken.None);
                return BackupShardRunResult.Completed;
            }

            if (backupSubmissionAttempted && string.IsNullOrWhiteSpace(shard.ClickHouseOperationId) && IsTransientShardFailure(ex, cancellationToken))
            {
                var recoveryResult = await TryRecoverUnknownSubmittedBackupShardAsync(scopedDb, scopedClickHouse, scopedAudit, scopedQueue, scopedStorage, backup, table, shard, endpoint, scopedOptions.CurrentValue.BackupSubmissionStatusCheckDelay, scopedOptions.CurrentValue.PollInterval, scopedOptions.CurrentValue.TransientShardRetryDelay, scopedOptions.CurrentValue.TransientShardMaxRetries, retryCounts, CancellationToken.None);
                if (recoveryResult is not null)
                {
                    return recoveryResult.Value;
                }

                _logger.Warning(ex, "Backup table {BackupTableId} {Database}.{Table} shard {ShardNumber} submission outcome is unknown and no matching ClickHouse operation was found; retry may use a fresh storage path.", table.Id, table.Database, table.Table, shard.SourceShardNumber);
            }

            var maxRetries = Math.Max(0, scopedOptions.CurrentValue.TransientShardMaxRetries);
            var attempt = retryCounts.AddOrUpdate(shard.Id, 1, (_, current) => current + 1);
            if (attempt <= maxRetries && IsTransientShardFailure(ex, cancellationToken))
            {
                var retryDelay = scopedOptions.CurrentValue.TransientShardRetryDelay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : scopedOptions.CurrentValue.TransientShardRetryDelay;
                failedEndpoints.GetOrAdd(shard.Id, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)).TryAdd(EndpointKey(endpoint), 0);
                _logger.Warning(ex, "Backup table {BackupTableId} {Database}.{Table} shard {ShardNumber} failed with a transient error; retry {RetryAttempt}/{MaxRetries} will be queued after {RetryDelay}.", table.Id, table.Database, table.Table, shard.SourceShardNumber, attempt, maxRetries, retryDelay);
                shard.Status = BackupTableStatus.Queued;
                shard.Error = ex.Message;
                shard.CompletedAt = null;
                shard.StartedAt = null;
                await scopedDb.SaveChangesAsync(CancellationToken.None);
                await scopedAudit.RecordAsync("shard-retry-scheduled", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = ex.Message, sourceShard = shard.SourceShardNumber, retryAttempt = attempt, maxRetries, retryDelaySeconds = retryDelay.TotalSeconds });
                await scopedQueue.ClearStartedClaimAsync(BackupRestoreQueueKind.Backup, shard.Id, CancellationToken.None);
                await Task.Delay(retryDelay, CancellationToken.None);
                scopedQueue.ReleaseInMemoryClaim(BackupRestoreQueueKind.Backup, shard.Id);
                return BackupShardRunResult.RetryLater;
            }

            _logger.Error(ex, "Backup table {BackupTableId} {Database}.{Table} shard {ShardNumber} failed.", table.Id, table.Database, table.Table, shard.SourceShardNumber);
            shard.Status = BackupTableStatus.Failed;
            shard.Error = ex.Message;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("shard-failed", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = ex.Message, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, CancellationToken.None);
            return BackupShardRunResult.Completed;
        }
    }

    private static async Task AssignNewShardAttemptStoragePathAsync(ChoboDbContext scopedDb, IAuditService scopedAudit, BackupTableEntity table, BackupTableShardEntity shard, CancellationToken cancellationToken)
    {
        var previousStoragePath = shard.StoragePath;
        shard.StoragePath = BuildShardAttemptStoragePath(table.StoragePath, shard.StoragePath, shard.SourceShardNumber);
        await scopedDb.SaveChangesAsync(cancellationToken);
        await scopedAudit.RecordAsync("shard-attempt-path-assigned", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, previousStoragePath, storagePath = shard.StoragePath });
    }

    private static string BuildShardAttemptStoragePath(string tableStoragePath, string shardStoragePath, int shardNumber)
    {
        var attemptIndex = shardStoragePath.IndexOf("/attempt-", StringComparison.Ordinal);
        var shardBasePath = attemptIndex >= 0 ? shardStoragePath[..attemptIndex] : shardStoragePath.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(shardBasePath))
        {
            var tablePath = tableStoragePath.TrimEnd('/');
            shardBasePath = string.IsNullOrWhiteSpace(tablePath) ? $"shards/shard-{shardNumber:0000}" : $"{tablePath}/shards/shard-{shardNumber:0000}";
        }

        return $"{shardBasePath}/attempt-{Guid.NewGuid():N}";
    }

    private async Task<BackupShardRunResult?> TryRecoverUnknownSubmittedBackupShardAsync(
        ChoboDbContext scopedDb,
        IClickHouseAdapter scopedClickHouse,
        IAuditService scopedAudit,
        BackupRestoreQueueApplicationService scopedQueue,
        IBackupStorageOperations scopedStorage,
        BackupEntity backup,
        BackupTableEntity table,
        BackupTableShardEntity shard,
        ClickHouseNodeEndpoint endpoint,
        TimeSpan statusCheckDelay,
        TimeSpan pollInterval,
        TimeSpan retryDelay,
        int maxRetries,
        ConcurrentDictionary<Guid, int> retryCounts,
        CancellationToken cancellationToken)
    {
        var delay = statusCheckDelay < TimeSpan.Zero ? TimeSpan.Zero : statusCheckDelay;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        ClickHouseDiscoveredOperation? discovered;
        try
        {
            discovered = await scopedClickHouse.FindLatestBackupOperationForPathAsync(endpoint, backup.SourceCluster!, backup.Target!, shard.StoragePath, cancellationToken);
        }
        catch (Exception lookupEx)
        {
            _logger.Warning(lookupEx, "Could not check ClickHouse backup status for backup table {BackupTableId} shard {BackupShardId} after uncertain submission failure.", table.Id, shard.Id);
            await scopedAudit.RecordAsync("clickhouse-operation-recovery-check-failed", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = lookupEx.Message, sourceShard = shard.SourceShardNumber, shard.StoragePath });
            return null;
        }

        if (discovered is null)
        {
            await scopedAudit.RecordAsync("clickhouse-operation-recovery-not-found", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { sourceShard = shard.SourceShardNumber, shard.StoragePath });
            return null;
        }

        shard.ClickHouseOperationId = discovered.OperationId;
        shard.ClickHouseStatus = discovered.Status;
        table.ClickHouseOperationId ??= discovered.OperationId;
        table.ClickHouseStatus = discovered.Status;
        await scopedDb.SaveChangesAsync(cancellationToken);
        await scopedAudit.RecordAsync("clickhouse-operation-recovered", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { clickHouseOperationId = discovered.OperationId, discovered.Status, sourceShard = shard.SourceShardNumber, shard.StoragePath });

        try
        {
            var finalClickHouseStatus = await PollBackupShardAsync(scopedDb, scopedClickHouse, endpoint, backup.SourceCluster!, backup.Id, shard, new ClickHouseOperationStatus(true, discovered.Status, discovered.Error), pollInterval, cancellationToken);
            if (finalClickHouseStatus is null || await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
            {
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                return BackupShardRunResult.Completed;
            }

            var backupSizeBytes = await MeasureBackupPathAsync(scopedStorage, backup.Target!, shard.StoragePath, cancellationToken);
            if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, cancellationToken))
            {
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
                return BackupShardRunResult.Completed;
            }

            shard.Status = BackupTableStatus.Succeeded;
            shard.ClickHouseStatus = finalClickHouseStatus;
            shard.Error = null;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            shard.BackupSizeBytes = backupSizeBytes;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { table.Database, table.Table, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, shard.ClickHouseStatus, shard.BackupSizeBytes, recovered = true });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, cancellationToken);
            return BackupShardRunResult.Completed;
        }
        catch (Exception recoveryEx)
        {
            if (await ReloadShardAndStopIfCanceledAsync(scopedDb, backup, table, shard, CancellationToken.None))
            {
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Backup, shard.Id, CancellationToken.None);
                return BackupShardRunResult.Completed;
            }

            var attempt = retryCounts.AddOrUpdate(shard.Id, 1, (_, current) => current + 1);
            if (attempt <= Math.Max(0, maxRetries) && IsTransientShardFailure(recoveryEx, cancellationToken))
            {
                var effectiveRetryDelay = retryDelay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : retryDelay;
                _logger.Warning(recoveryEx, "Recovered ClickHouse backup operation {OperationId} for table {BackupTableId} shard {BackupShardId} hit a transient error; retry {RetryAttempt}/{MaxRetries} will resume the recovered operation after {RetryDelay}.", shard.ClickHouseOperationId, table.Id, shard.Id, attempt, maxRetries, effectiveRetryDelay);
                shard.Status = BackupTableStatus.Queued;
                shard.Error = recoveryEx.Message;
                shard.CompletedAt = null;
                shard.StartedAt = null;
                await scopedDb.SaveChangesAsync(CancellationToken.None);
                await scopedAudit.RecordAsync("shard-retry-scheduled", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = recoveryEx.Message, sourceShard = shard.SourceShardNumber, retryAttempt = attempt, maxRetries, retryDelaySeconds = effectiveRetryDelay.TotalSeconds, clickHouseOperationId = shard.ClickHouseOperationId, recovered = true });
                await scopedQueue.ClearStartedClaimAsync(BackupRestoreQueueKind.Backup, shard.Id, CancellationToken.None);
                await Task.Delay(effectiveRetryDelay, CancellationToken.None);
                scopedQueue.ReleaseInMemoryClaim(BackupRestoreQueueKind.Backup, shard.Id);
                return BackupShardRunResult.RetryLater;
            }

            _logger.Error(recoveryEx, "Recovered ClickHouse backup operation {OperationId} for table {BackupTableId} shard {BackupShardId} failed while polling.", shard.ClickHouseOperationId, table.Id, shard.Id);
            shard.Status = BackupTableStatus.Failed;
            shard.Error = recoveryEx.Message;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("shard-failed", AuditEntityType.BackupTableShard, shard.Id.ToString(), new { error = recoveryEx.Message, sourceShard = shard.SourceShardNumber, clickHouseOperationId = shard.ClickHouseOperationId, recovered = true });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, shard.Id, CancellationToken.None);
            return BackupShardRunResult.Completed;
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
        return objects.Sum(x => x.SizeBytes);
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

    private int EffectiveMaxDop(ClickHouseClusterEntity cluster) =>
        Math.Max(1, cluster.BackupRestoreMaxDop > 0 ? cluster.BackupRestoreMaxDop : options.CurrentValue.MaxDop <= 0 ? 3 : options.CurrentValue.MaxDop);


    private static bool IsReplicatedMergeTreeEngine(string engine) =>
        engine.Contains("Replicated", StringComparison.OrdinalIgnoreCase) && engine.Contains("MergeTree", StringComparison.OrdinalIgnoreCase);


    private async Task<IReadOnlyList<ClickHouseShardReplicaInfo>> GetAvailableBackupReplicaCandidatesAsync(IClickHouseAdapter adapter, ClickHouseClusterEntity cluster, IReadOnlyList<ClickHouseShardReplicaInfo> shardReplicas, CancellationToken cancellationToken)
    {
        if (shardReplicas.Count <= 1)
        {
            return shardReplicas;
        }

        var availableReplicas = new List<ClickHouseShardReplicaInfo>(shardReplicas.Count);
        foreach (var replica in shardReplicas)
        {
            try
            {
                await adapter.ExecuteAsync(replica.Endpoint, cluster, "SELECT version()", cancellationToken);
                availableReplicas.Add(replica);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ClickHouse replica {Host}:{Port} for cluster {ClusterId} shard {ShardNumber} is unavailable during backup candidate selection.", replica.Host, replica.Port, cluster.Id, replica.ShardNumber);
            }
        }

        return availableReplicas.Count == 0
            ? shardReplicas
            : availableReplicas;
    }

    private async Task<string?> GetParentTablePathAsync(Guid? parentFullBackupTableId, CancellationToken cancellationToken) =>
        parentFullBackupTableId is null
            ? null
            : await db.BackupTables
                .Where(x => x.Id == parentFullBackupTableId)
                .Select(x => x.StoragePath)
                .FirstOrDefaultAsync(cancellationToken);

    private static async Task<string?> GetParentShardPathAsync(ChoboDbContext db, Guid? parentFullBackupTableShardId, CancellationToken cancellationToken) =>
        parentFullBackupTableShardId is null
            ? null
            : await db.BackupTableShards
                .Where(x => x.Id == parentFullBackupTableShardId)
                .Select(x => x.StoragePath)
                .FirstOrDefaultAsync(cancellationToken);




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

    private static string EndpointKey(ClickHouseNodeEndpoint endpoint) => $"{endpoint.Host}:{endpoint.Port}:{endpoint.UseTls}";

    private static bool IsTransientShardFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException || current is TaskCanceledException || current is System.Net.Http.HttpRequestException || current is System.Net.Sockets.SocketException)
            {
                return true;
            }

            var message = current.Message;
            if (IsStorageOrCredentialFailure(message))
            {
                return false;
            }

            if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("temporar", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("transient", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection timed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no route to host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("host not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStorageOrCredentialFailure(string message) =>
        message.Contains("S3_ERROR", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Aws::S3", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("BackupWriterS3", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("S3Exception", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("InvalidAccessKeyId", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("REQUIRED_PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase);

}
