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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Backup garbage collection failed.");
            }

            var interval = options.Value.Interval <= TimeSpan.Zero ? TimeSpan.FromHours(1) : options.Value.Interval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> pending;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await MarkFailedBackupsAsync(db, audit, cancellationToken);
            await MarkOrphanIncrementalBackupsAsync(db, audit, cancellationToken);
            pending = await db.Backups
                .Where(x => x.Status == BackupRunStatus.FailedBackupDeleteRequested)
                .OrderBy(x => x.DeletionRequestedAt ?? x.CreatedAt)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        await ForEachAsync(pending, options.Value.MaxDop, async id =>
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BackupCleanupService>()
                .CleanupAsync(id, BackupRunStatus.FailedBackupDeletedByGarbageCollector, "failed-backup-garbage-collector", cancellationToken);
        }, cancellationToken);
    }

    private async Task MarkFailedBackupsAsync(ChoboDbContext db, IAuditService audit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var backups = await db.Backups
            .Include(x => x.Policy)
            .Where(x => (x.Status == BackupRunStatus.Failed || x.Status == BackupRunStatus.PartiallySucceeded) &&
                        x.Policy != null &&
                        x.Policy.FailedBackupRetentionMode == FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure)
            .ToListAsync(cancellationToken);

        foreach (var backup in backups)
        {
            await MarkDependentIncrementalBackupsAsync(db, audit, backup.Id, now, "failed-parent-garbage-collector", cancellationToken);
            backup.Status = BackupRunStatus.FailedBackupDeleteRequested;
            backup.DeletionReason = "failed-backup-garbage-collector";
            backup.DeletionRequestedAt ??= now;
            backup.DeletionError = null;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("failed-backup-garbage-collection-requested", AuditEntityType.Backup, backup.Id.ToString(), new { backup.PolicyId, backup.Status });
        }
    }

    private static async Task MarkDependentIncrementalBackupsAsync(ChoboDbContext db, IAuditService audit, Guid fullBackupId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
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
    }

    private static async Task MarkOrphanIncrementalBackupsAsync(ChoboDbContext db, IAuditService audit, CancellationToken cancellationToken)
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
    }

    private static readonly BackupRunStatus[] DeletedStatuses =
    [
        BackupRunStatus.ManualDeleteRequested,
        BackupRunStatus.ManualDeleted,
        BackupRunStatus.FailedBackupDeleteRequested,
        BackupRunStatus.FailedBackupDeletedByGarbageCollector,
        BackupRunStatus.BackupExpiredDeleteStarted,
        BackupRunStatus.BackupExpiredDeleted
    ];

    private static async Task ForEachAsync(IEnumerable<Guid> ids, int maxDop, Func<Guid, Task> action, CancellationToken cancellationToken)
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
