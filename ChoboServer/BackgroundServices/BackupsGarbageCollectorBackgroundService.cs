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
    IOptions<BackupsGarbageCollectorOptions> options,
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

            var interval = options.Value.Interval <= TimeSpan.Zero ? TimeSpan.FromHours(1) : options.Value.Interval;
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

    public Task<BackupGarbageCollectorStatusDto> RunOnceAsync(CancellationToken cancellationToken = default) =>
        RunOnceAsync("manual", cancellationToken);

    public async Task<BackupGarbageCollectorStatusDto> RunOnceAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return GetStatus();
        }

        try
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

            try
            {
                var result = await RunCoreAsync(cancellationToken);
                _lastMarkedCount = result.MarkedCount;
                _lastPendingCleanupCount = result.PendingCleanupCount;
                _lastCleanedCount = result.CleanedCount;
                _lastFailedCount = result.FailedCount;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                throw;
            }
            finally
            {
                _isRunning = false;
                _currentRunReason = "between-runs";
                _lastCompletedAt = timeProvider.GetUtcNow();
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
            var failedMarked = await MarkFailedBackupsAsync(db, audit, cancellationToken);
            var orphanedMarked = await MarkOrphanIncrementalBackupsAsync(db, audit, cancellationToken);
            markedCount = failedMarked + orphanedMarked;
            pending = await db.Backups
                .Where(x => x.Status == BackupRunStatus.ManualDeleteRequested ||
                            x.Status == BackupRunStatus.BackupExpiredDeleteStarted ||
                            x.Status == BackupRunStatus.FailedBackupDeleteRequested ||
                            (x.Status == BackupRunStatus.Canceled && x.DeletionReason == "canceled" && x.DeletedAt == null))
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
        }

        var cleanedCount = 0;
        var failedCount = 0;
        await ForEachAsync(pending, options.Value.MaxDop, async pendingCleanup =>
        {
            try
            {
                using var scope = services.CreateScope();
                var cleaned = await scope.ServiceProvider.GetRequiredService<BackupCleanupService>()
                    .CleanupAsync(pendingCleanup.Id, pendingCleanup.FinalStatus, pendingCleanup.Reason, cancellationToken);
                if (cleaned) Interlocked.Increment(ref cleanedCount);
                else Interlocked.Increment(ref failedCount);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedCount);
                _logger.Warning(ex, "Backup cleanup failed during garbage collection for backup {BackupId}.", pendingCleanup.Id);
            }
        }, cancellationToken);

        return new BackupGarbageCollectorRunResult(markedCount, pending.Count, cleanedCount, failedCount);
    }

    private async Task<int> MarkFailedBackupsAsync(ChoboDbContext db, IAuditService audit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var backups = await db.Backups
            .Include(x => x.Policy)
            .Where(x => (x.Status == BackupRunStatus.Failed || x.Status == BackupRunStatus.PartiallySucceeded) &&
                        x.Policy != null &&
                        x.Policy.FailedBackupRetentionMode == FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure)
            .ToListAsync(cancellationToken);

        var markedCount = 0;
        foreach (var backup in backups)
        {
            markedCount += await MarkDependentIncrementalBackupsAsync(db, audit, backup.Id, now, "failed-parent-garbage-collector", cancellationToken);
            backup.Status = BackupRunStatus.FailedBackupDeleteRequested;
            backup.DeletionReason = "failed-backup-garbage-collector";
            backup.DeletionRequestedAt ??= now;
            backup.DeletionError = null;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("failed-backup-garbage-collection-requested", AuditEntityType.Backup, backup.Id.ToString(), new { backup.PolicyId, backup.Status });
            markedCount++;
        }

        return markedCount;
    }

    private static async Task<int> MarkDependentIncrementalBackupsAsync(ChoboDbContext db, IAuditService audit, Guid fullBackupId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var deletedStatuses = DeletedStatuses;
        var dependentIds = await db.BackupTables
            .Where(x => x.ParentFullBackupTable != null && x.ParentFullBackupTable.BackupId == fullBackupId)
            .Select(x => x.BackupId)
            .Concat(db.BackupTableShards
                .Where(x => x.ParentFullBackupTableShard != null && x.ParentFullBackupTableShard.BackupTable!.BackupId == fullBackupId)
                .Select(x => x.BackupTable!.BackupId))
            .Distinct()
            .ToListAsync(cancellationToken);
        var dependents = await db.Backups
            .Where(x => dependentIds.Contains(x.Id) && !deletedStatuses.Contains(x.Status))
            .ToListAsync(cancellationToken);
        foreach (var dependent in dependents)
        {
            dependent.Status = BackupRunStatus.FailedBackupDeleteRequested;
            dependent.DeletionReason = reason;
            dependent.DeletionRequestedAt ??= now;
            dependent.DeletionError = null;
            await audit.RecordAsync("dependent-failed-backup-garbage-collection-requested", AuditEntityType.Backup, dependent.Id.ToString(), new { parentBackupId = fullBackupId, reason });
        }

        return dependents.Count;
    }

    private static async Task<int> MarkOrphanIncrementalBackupsAsync(ChoboDbContext db, IAuditService audit, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var deletedStatuses = DeletedStatuses;
        var orphanIds = await db.BackupTables
            .Where(x => x.EffectiveBackupType == BackupType.Incremental &&
                        (x.ParentFullBackupTable == null || deletedStatuses.Contains(x.ParentFullBackupTable.Backup!.Status)))
            .Select(x => x.BackupId)
            .Concat(db.BackupTableShards
                .Where(x => x.EffectiveBackupType == BackupType.Incremental &&
                            (x.ParentFullBackupTableShard == null || deletedStatuses.Contains(x.ParentFullBackupTableShard.BackupTable!.Backup!.Status)))
                .Select(x => x.BackupTable!.BackupId))
            .Distinct()
            .ToListAsync(cancellationToken);
        var orphans = await db.Backups
            .Where(x => orphanIds.Contains(x.Id) && !deletedStatuses.Contains(x.Status))
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            orphan.Status = BackupRunStatus.FailedBackupDeleteRequested;
            orphan.DeletionReason = "orphaned-incremental-parent-missing";
            orphan.DeletionRequestedAt ??= now;
            orphan.DeletionError = null;
            await audit.RecordAsync("orphaned-incremental-garbage-collection-requested", AuditEntityType.Backup, orphan.Id.ToString(), new { reason = orphan.DeletionReason });
        }

        return orphans.Count;
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


