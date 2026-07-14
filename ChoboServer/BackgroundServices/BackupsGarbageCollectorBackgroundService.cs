using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class BackupsGarbageCollectorBackgroundService(
    IServiceProvider services,
    IOptionsMonitor<BackupsGarbageCollectorOptions> options,
    TimeProvider timeProvider,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupsGarbageCollectorBackgroundService>();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private bool _isRunning;
    private string _currentRunReason = "between-runs";
    private DateTimeOffset? _lastStartedAt;
    private DateTimeOffset? _lastCompletedAt;
    private string? _lastError;
    private int _lastMarkedCount;
    private int _lastPendingCleanupCount;
    private int _lastCleanedCount;
    private int _lastFailedCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync("scheduled", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Backup garbage collection failed.");
            }

            var interval = options.CurrentValue.Interval <= TimeSpan.Zero ? TimeSpan.FromHours(1) : options.CurrentValue.Interval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    public BackupGarbageCollectorStatusDto GetStatus()
    {
        lock (_runLock)
        {
            return ToStatus();
        }
    }

    public async Task<IReadOnlyList<BackupGarbageCollectorQueueItemDto>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        return await BuildQueueItemsAsync(db, cancellationToken);
    }

    public Task<BackupGarbageCollectorStatusDto> RunOnceAsync(CancellationToken cancellationToken = default) =>
        RunOnceAsync("manual", cancellationToken);

    public async Task<BackupGarbageCollectorStatusDto> RunOnceAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            _logger.Information("Backup garbage collection run requested for {Reason}, but another run is already active with reason {CurrentRunReason}.", reason, _currentRunReason);
            return GetStatus();
        }

        try
        {
            BeginRun(reason);
            try
            {
                _logger.Information("Backup garbage collection run started. Reason: {Reason}.", _currentRunReason);
                var result = await RunCoreAsync(cancellationToken);
                ApplyResult(result);
                _logger.Information("Backup garbage collection run completed. Marked={MarkedCount}, pending={PendingCleanupCount}, cleaned={CleanedCount}, failed={FailedCount}.", result.MarkedCount, result.PendingCleanupCount, result.CleanedCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.Error(ex, "Backup garbage collection run failed. Reason: {Reason}.", _currentRunReason);
                throw;
            }
            finally
            {
                EndRun();
            }

            return ToStatus();
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<BackupGarbageCollectorStatusDto> RunOneAsync(Guid backupId, string reason, CancellationToken cancellationToken = default)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            _logger.Information("Single backup garbage collection requested for backup {BackupId}, but another run is already active with reason {CurrentRunReason}.", backupId, _currentRunReason);
            return GetStatus();
        }

        try
        {
            BeginRun(string.IsNullOrWhiteSpace(reason) ? $"manual-one:{backupId}" : reason);
            try
            {
                _logger.Information("Single backup garbage collection started for backup {BackupId}. Reason: {Reason}.", backupId, _currentRunReason);
                var result = await RunOneCoreAsync(backupId, cancellationToken);
                ApplyResult(result);
                _logger.Information("Single backup garbage collection completed for backup {BackupId}. Marked={MarkedCount}, pending={PendingCleanupCount}, cleaned={CleanedCount}, failed={FailedCount}.", backupId, result.MarkedCount, result.PendingCleanupCount, result.CleanedCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.Error(ex, "Single backup garbage collection failed for backup {BackupId}.", backupId);
                throw;
            }
            finally
            {
                EndRun();
            }

            return ToStatus();
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<BackupGarbageCollectorRunResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        List<PendingBackupCleanup> pending;
        int markedCount;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            var evaluator = scope.ServiceProvider.GetRequiredService<BackupGarbageCollectionEvaluationService>();
            _logger.Information("Backup garbage collection mark phase started.");
            var queuePruned = await PruneCompletedQueueItemsAsync(db, audit, cancellationToken);
            var failedMarked = await MarkFailedBackupsAsync(db, audit, evaluator, cancellationToken);
            var orphanedMarked = await MarkOrphanIncrementalBackupsAsync(db, audit, cancellationToken);
            markedCount = failedMarked + orphanedMarked;
            _logger.Information("Backup garbage collection mark phase completed. Completed queue rows pruned={QueuePrunedCount}, failed marked={FailedMarkedCount}, orphaned marked={OrphanedMarkedCount}, total marked={MarkedCount}.", queuePruned, failedMarked, orphanedMarked, markedCount);
            pending = await GetPendingCleanupAsync(db, null, cancellationToken);
            _logger.Information("Backup garbage collection cleanup queue built with {PendingCleanupCount} item(s).", pending.Count);
        }

        return await CleanupPendingAsync(pending, markedCount, cancellationToken);
    }

    private async Task<BackupGarbageCollectorRunResult> RunOneCoreAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var markedCount = 0;
        PendingBackupCleanup? pending;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            var evaluator = scope.ServiceProvider.GetRequiredService<BackupGarbageCollectionEvaluationService>();
            markedCount = await MarkOneIfEligibleAsync(db, audit, evaluator, backupId, cancellationToken);
            pending = (await GetPendingCleanupAsync(db, backupId, cancellationToken)).SingleOrDefault();
            _logger.Information("Single backup garbage collection queue lookup for backup {BackupId}: marked={MarkedCount}, pending={IsPending}.", backupId, markedCount, pending is not null);
        }

        if (pending is null)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var alreadyCleaned = await db.Backups
                .AsNoTracking()
                .AnyAsync(x => x.Id == backupId && x.DeletedAt != null && DeletedStatuses.Contains(x.Status), cancellationToken);
            if (alreadyCleaned)
            {
                _logger.Information("Single backup garbage collection found backup {BackupId} was already cleaned before the manual item run.", backupId);
                return new BackupGarbageCollectorRunResult(markedCount, 0, 0, 0);
            }

            _logger.Information("Single backup garbage collection found no pending cleanup item for backup {BackupId}.", backupId);
            return new BackupGarbageCollectorRunResult(markedCount, 0, 0, 1);
        }

        return await CleanupPendingAsync([pending], markedCount, cancellationToken);
    }

    private async Task<BackupGarbageCollectorRunResult> CleanupPendingAsync(IReadOnlyList<PendingBackupCleanup> pending, int markedCount, CancellationToken cancellationToken)
    {
        var cleanedCount = 0;
        var failedCount = 0;
        await ForEachAsync(pending, options.CurrentValue.MaxDop, async pendingCleanup =>
        {
            try
            {
                _logger.Information("Backup garbage collection cleanup started for backup {BackupId}. FinalStatus={FinalStatus}, reason={Reason}.", pendingCleanup.Id, pendingCleanup.FinalStatus, pendingCleanup.Reason);
                using var scope = services.CreateScope();
                var cleaned = await scope.ServiceProvider.GetRequiredService<BackupCleanupService>()
                    .CleanupAsync(pendingCleanup.Id, pendingCleanup.FinalStatus, pendingCleanup.Reason, cancellationToken);
                if (cleaned)
                {
                    Interlocked.Increment(ref cleanedCount);
                    _logger.Information("Backup garbage collection cleanup succeeded for backup {BackupId}.", pendingCleanup.Id);
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                    _logger.Warning("Backup garbage collection cleanup did not complete for backup {BackupId}; cleanup service returned false.", pendingCleanup.Id);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedCount);
                _logger.Warning(ex, "Backup cleanup failed during garbage collection for backup {BackupId}.", pendingCleanup.Id);
            }
        }, cancellationToken);

        return new BackupGarbageCollectorRunResult(markedCount, pending.Count, cleanedCount, failedCount);
    }

    private async Task<int> PruneCompletedQueueItemsAsync(ChoboDbContext db, IAuditService audit, CancellationToken cancellationToken)
    {
        var deleted = await db.BackupRestoreQueueItems
            .Where(x => x.CompletedAt != null)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            _logger.Information("Backup garbage collection pruned {QueuePrunedCount} completed backup/restore queue row(s).", deleted);
            await audit.RecordAsync("queue-completed-pruned", AuditEntityType.BackupGarbageCollector, null, new { deleted });
        }

        return deleted;
    }

    private async Task<int> MarkFailedBackupsAsync(
        ChoboDbContext db,
        IAuditService audit,
        BackupGarbageCollectionEvaluationService evaluator,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var failedIds = await db.Backups.AsNoTracking()
            .Where(x => x.Status == BackupRunStatus.Failed)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var eligibleIds = new List<Guid>();
        foreach (var failedId in failedIds)
        {
            if ((await evaluator.EvaluateAsync(failedId, cancellationToken))?.EligibleForDeletion == true)
            {
                eligibleIds.Add(failedId);
            }
        }
        var backups = await db.Backups
            .Include(x => x.Policy)
            .Where(x => eligibleIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        _logger.Information("Backup garbage collection found {FailedBackupCount} failed backup(s) eligible for cleanup marking.", backups.Count);
        if (backups.Count == 0)
        {
            return 0;
        }

        var failedBackupIds = backups.Select(x => x.Id).ToList();
        var dependentMarkedCount = await MarkDependentIncrementalBackupsAsync(db, audit, failedBackupIds, now, "failed-parent-garbage-collector", cancellationToken);
        foreach (var backup in backups)
        {
            MarkBackupForGarbageCollection(backup, now, "failed-backup-garbage-collector");
            _logger.Information("Backup garbage collection marked failed backup {BackupId} for cleanup. PolicyId={PolicyId}.", backup.Id, backup.PolicyId);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var backup in backups)
        {
            await audit.RecordAsync("failed-backup-garbage-collection-requested", AuditEntityType.Backup, backup.Id.ToString(), new { backup.PolicyId, backup.Status });
        }

        return dependentMarkedCount + backups.Count;
    }

    private async Task<int> MarkOneIfEligibleAsync(
        ChoboDbContext db,
        IAuditService audit,
        BackupGarbageCollectionEvaluationService evaluator,
        Guid backupId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var backup = await db.Backups
            .Include(x => x.Policy)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null)
        {
            return 0;
        }

        if (backup.Status == BackupRunStatus.Failed &&
            (await evaluator.EvaluateAsync(backup.Id, cancellationToken))?.EligibleForDeletion == true)
        {
            var markedCount = await MarkDependentIncrementalBackupsAsync(db, audit, [backup.Id], now, "failed-parent-garbage-collector", cancellationToken);
            MarkBackupForGarbageCollection(backup, now, "failed-backup-garbage-collector");
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("failed-backup-garbage-collection-requested", AuditEntityType.Backup, backup.Id.ToString(), new { backup.PolicyId, backup.Status });
            _logger.Information("Single backup garbage collection marked failed backup {BackupId} for cleanup.", backup.Id);
            return markedCount + 1;
        }

        if (await IsOrphanIncrementalAsync(db, backup.Id, cancellationToken))
        {
            MarkBackupForGarbageCollection(backup, now, "orphaned-incremental-parent-missing");
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("orphaned-incremental-garbage-collection-requested", AuditEntityType.Backup, backup.Id.ToString(), new { reason = backup.DeletionReason });
            _logger.Information("Single backup garbage collection marked orphaned incremental backup {BackupId} for cleanup.", backup.Id);
            return 1;
        }

        return 0;
    }

    private static async Task<int> MarkDependentIncrementalBackupsAsync(ChoboDbContext db, IAuditService audit, IReadOnlyList<Guid> fullBackupIds, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        if (fullBackupIds.Count == 0)
        {
            return 0;
        }

        var deletedStatuses = DeletedStatuses;
        var parentTableIds = await db.BackupTables
            .Where(x => fullBackupIds.Contains(x.BackupId))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var parentShardIds = await db.BackupTableShards
            .Where(x => x.BackupTable != null && fullBackupIds.Contains(x.BackupTable.BackupId))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var dependentIds = await db.BackupTables
            .Where(x => x.ParentFullBackupTableId != null && parentTableIds.Contains(x.ParentFullBackupTableId.Value))
            .Select(x => x.BackupId)
            .Concat(db.BackupTableShards
                .Where(x => x.ParentFullBackupTableShardId != null && parentShardIds.Contains(x.ParentFullBackupTableShardId.Value))
                .Select(x => x.BackupTable!.BackupId))
            .Distinct()
            .ToListAsync(cancellationToken);
        var dependents = await db.Backups
            .Where(x => dependentIds.Contains(x.Id) && !deletedStatuses.Contains(x.Status))
            .ToListAsync(cancellationToken);
        foreach (var dependent in dependents)
        {
            MarkBackupForGarbageCollection(dependent, now, reason);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var dependent in dependents)
        {
            await audit.RecordAsync("dependent-failed-backup-garbage-collection-requested", AuditEntityType.Backup, dependent.Id.ToString(), new { parentBackupIds = fullBackupIds, reason });
        }

        return dependents.Count;
    }

    private static async Task<int> MarkOrphanIncrementalBackupsAsync(ChoboDbContext db, IAuditService audit, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var orphanIds = await FindOrphanIncrementalBackupIdsAsync(db, cancellationToken);
        var orphans = await db.Backups
            .Where(x => orphanIds.Contains(x.Id) && !DeletedStatuses.Contains(x.Status))
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            MarkBackupForGarbageCollection(orphan, now, "orphaned-incremental-parent-missing");
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            await audit.RecordAsync("orphaned-incremental-garbage-collection-requested", AuditEntityType.Backup, orphan.Id.ToString(), new { reason = orphan.DeletionReason });
        }

        return orphans.Count;
    }

    private static async Task<IReadOnlyList<BackupGarbageCollectorQueueItemDto>> BuildQueueItemsAsync(ChoboDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.Backups
            .AsNoTracking()
            .Where(x => x.Status == BackupRunStatus.ManualDeleteRequested ||
                        x.Status == BackupRunStatus.BackupExpiredDeleteStarted ||
                        x.Status == BackupRunStatus.FailedBackupDeleteRequested ||
                        (x.Status == BackupRunStatus.Canceled && x.DeletionReason == "canceled" && x.DeletedAt == null))
            .Select(x => new BackupGarbageCollectorQueueItemDto(
                x.Id,
                "backup",
                x.Status,
                x.Status == BackupRunStatus.ManualDeleteRequested ? BackupRunStatus.ManualDeleted :
                x.Status == BackupRunStatus.BackupExpiredDeleteStarted ? BackupRunStatus.BackupExpiredDeleted :
                x.Status == BackupRunStatus.Canceled ? BackupRunStatus.Canceled : BackupRunStatus.FailedBackupDeletedByGarbageCollector,
                x.Status == BackupRunStatus.ManualDeleteRequested ? "manual" :
                x.Status == BackupRunStatus.BackupExpiredDeleteStarted ? "retention" :
                x.Status == BackupRunStatus.Canceled ? "canceled-backup-garbage-collector" : x.DeletionReason ?? "failed-backup-garbage-collector",
                x.CreatedAt,
                x.DeletionRequestedAt,
                x.DeletionAttemptCount,
                x.DeletionError))
            .ToListAsync(cancellationToken);

        var pendingIds = pending.Select(x => x.EntityId).ToHashSet();
        var failed = await db.Backups
            .AsNoTracking()
            .Where(x => !pendingIds.Contains(x.Id) &&
                        x.Status == BackupRunStatus.Failed &&
                        x.Policy != null &&
                        x.Policy.FailedBackupRetentionMode == FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure)
            .Select(x => new BackupGarbageCollectorQueueItemDto(x.Id, "backup", x.Status, BackupRunStatus.FailedBackupDeletedByGarbageCollector, "failed-backup-garbage-collector", x.CreatedAt, x.DeletionRequestedAt, x.DeletionAttemptCount, x.DeletionError))
            .ToListAsync(cancellationToken);

        var excludedIds = pendingIds.Concat(failed.Select(x => x.EntityId)).ToHashSet();
        var orphanIds = await FindOrphanIncrementalBackupIdsAsync(db, cancellationToken);
        var orphans = await db.Backups
            .AsNoTracking()
            .Where(x => orphanIds.Contains(x.Id) && !excludedIds.Contains(x.Id) && !DeletedStatuses.Contains(x.Status))
            .Select(x => new BackupGarbageCollectorQueueItemDto(x.Id, "backup", x.Status, BackupRunStatus.FailedBackupDeletedByGarbageCollector, "orphaned-incremental-parent-missing", x.CreatedAt, x.DeletionRequestedAt, x.DeletionAttemptCount, x.DeletionError))
            .ToListAsync(cancellationToken);

        return pending.Concat(failed).Concat(orphans)
            .OrderBy(x => x.DeletionRequestedAt ?? x.CreatedAt)
            .ThenBy(x => x.EntityId)
            .ToList();
    }

    private static async Task<List<PendingBackupCleanup>> GetPendingCleanupAsync(ChoboDbContext db, Guid? backupId, CancellationToken cancellationToken) =>
        await db.Backups
            .Where(x => (backupId == null || x.Id == backupId.Value) &&
                        (x.Status == BackupRunStatus.ManualDeleteRequested ||
                         x.Status == BackupRunStatus.BackupExpiredDeleteStarted ||
                         x.Status == BackupRunStatus.FailedBackupDeleteRequested ||
                         (x.Status == BackupRunStatus.Canceled && x.DeletionReason == "canceled" && x.DeletedAt == null)))
            .OrderBy(x => x.DeletionRequestedAt ?? x.CreatedAt)
            .Select(x => new PendingBackupCleanup(
                x.Id,
                x.Status == BackupRunStatus.ManualDeleteRequested ? BackupRunStatus.ManualDeleted :
                x.Status == BackupRunStatus.BackupExpiredDeleteStarted ? BackupRunStatus.BackupExpiredDeleted :
                x.Status == BackupRunStatus.Canceled ? BackupRunStatus.Canceled : BackupRunStatus.FailedBackupDeletedByGarbageCollector,
                x.Status == BackupRunStatus.ManualDeleteRequested ? "manual" :
                x.Status == BackupRunStatus.BackupExpiredDeleteStarted ? "retention" :
                x.Status == BackupRunStatus.Canceled ? "canceled-backup-garbage-collector" : "failed-backup-garbage-collector"))
            .ToListAsync(cancellationToken);

    private static Task<List<Guid>> FindOrphanIncrementalBackupIdsAsync(ChoboDbContext db, CancellationToken cancellationToken)
    {
        var deletedStatuses = DeletedStatuses;
        return db.BackupTables
            .Where(x => x.EffectiveBackupType == BackupType.Incremental &&
                        ((x.ParentFullBackupId != null &&
                          !db.Backups.Any(parent => parent.Id == x.ParentFullBackupId.Value && !deletedStatuses.Contains(parent.Status))) ||
                         (x.ParentFullBackupId == null &&
                          !db.BackupTableShards.Any(shard =>
                              shard.BackupTableId == x.Id &&
                              shard.EffectiveBackupType == BackupType.Incremental &&
                              shard.ParentFullBackupId != null &&
                              db.Backups.Any(parent => parent.Id == shard.ParentFullBackupId.Value && !deletedStatuses.Contains(parent.Status))))))
            .Select(x => x.BackupId)
            .Concat(db.BackupTableShards
                .Where(x => x.EffectiveBackupType == BackupType.Incremental &&
                            (x.ParentFullBackupId == null ||
                             !db.Backups.Any(parent => parent.Id == x.ParentFullBackupId.Value && !deletedStatuses.Contains(parent.Status))))
                .Select(x => x.BackupTable!.BackupId))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private static async Task<bool> IsOrphanIncrementalAsync(ChoboDbContext db, Guid backupId, CancellationToken cancellationToken) =>
        await db.BackupTables.AnyAsync(x =>
            x.BackupId == backupId &&
            x.EffectiveBackupType == BackupType.Incremental &&
            ((x.ParentFullBackupId != null &&
              !db.Backups.Any(parent => parent.Id == x.ParentFullBackupId.Value && !DeletedStatuses.Contains(parent.Status))) ||
             (x.ParentFullBackupId == null &&
              !db.BackupTableShards.Any(shard =>
                  shard.BackupTableId == x.Id &&
                  shard.EffectiveBackupType == BackupType.Incremental &&
                  shard.ParentFullBackupId != null &&
                  db.Backups.Any(parent => parent.Id == shard.ParentFullBackupId.Value && !DeletedStatuses.Contains(parent.Status))))),
            cancellationToken) ||
        await db.BackupTableShards.AnyAsync(x =>
            x.BackupTable!.BackupId == backupId &&
            x.EffectiveBackupType == BackupType.Incremental &&
            (x.ParentFullBackupId == null ||
             !db.Backups.Any(parent => parent.Id == x.ParentFullBackupId.Value && !DeletedStatuses.Contains(parent.Status))),
            cancellationToken);

    private static void MarkBackupForGarbageCollection(BackupEntity backup, DateTimeOffset now, string reason)
    {
        backup.Status = BackupRunStatus.FailedBackupDeleteRequested;
        backup.DeletionReason = reason;
        backup.DeletionRequestedAt ??= now;
        backup.DeletionError = null;
    }

    private void BeginRun(string reason)
    {
        _isRunning = true;
        _currentRunReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason;
        _lastStartedAt = timeProvider.GetUtcNow();
        _lastCompletedAt = null;
        _lastError = null;
        _lastMarkedCount = 0;
        _lastPendingCleanupCount = 0;
        _lastCleanedCount = 0;
        _lastFailedCount = 0;
    }

    private void ApplyResult(BackupGarbageCollectorRunResult result)
    {
        _lastMarkedCount = result.MarkedCount;
        _lastPendingCleanupCount = result.PendingCleanupCount;
        _lastCleanedCount = result.CleanedCount;
        _lastFailedCount = result.FailedCount;
    }

    private void EndRun()
    {
        _isRunning = false;
        _currentRunReason = "between-runs";
        _lastCompletedAt = timeProvider.GetUtcNow();
    }

    private BackupGarbageCollectorStatusDto ToStatus() =>
        new(_isRunning, _currentRunReason, _lastStartedAt, _lastCompletedAt, _lastError, _lastMarkedCount, _lastPendingCleanupCount, _lastCleanedCount, _lastFailedCount);

    private sealed record PendingBackupCleanup(Guid Id, BackupRunStatus FinalStatus, string Reason);
    private sealed record BackupGarbageCollectorRunResult(int MarkedCount, int PendingCleanupCount, int CleanedCount, int FailedCount);

    private static readonly BackupRunStatus[] DeletedStatuses =
    [
        BackupRunStatus.Canceled,
        BackupRunStatus.ManualDeleteRequested,
        BackupRunStatus.ManualDeleted,
        BackupRunStatus.FailedBackupDeleteRequested,
        BackupRunStatus.FailedBackupDeletedByGarbageCollector,
        BackupRunStatus.BackupExpiredDeleteStarted,
        BackupRunStatus.BackupExpiredDeleted
    ];

    private static async Task ForEachAsync<T>(IEnumerable<T> ids, int maxDop, Func<T, Task> action, CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, maxDop));
        var tasks = ids.Select(async id =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try { await action(id); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
    }
}
