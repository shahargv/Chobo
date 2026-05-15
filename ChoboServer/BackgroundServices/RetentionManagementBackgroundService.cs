using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class RetentionManagementBackgroundService(
    IServiceProvider services,
    IOptions<RetentionManagementOptions> options,
    TimeProvider timeProvider,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<RetentionManagementBackgroundService>();

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
                _logger.Error(ex, "Backup retention management failed.");
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
            await MarkExpiredAsync(db, audit, cancellationToken);
            pending = await db.Backups
                .Where(x => x.Status == BackupRunStatus.ManualDeleteRequested || x.Status == BackupRunStatus.BackupExpiredDeleteStarted)
                .OrderBy(x => x.DeletionRequestedAt ?? x.CreatedAt)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        await ForEachAsync(pending, options.Value.MaxDop, async id =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var backup = await db.Backups.Where(x => x.Id == id).Select(x => new { x.Status }).FirstOrDefaultAsync(cancellationToken);
            if (backup is null)
            {
                return;
            }

            var finalStatus = backup.Status == BackupRunStatus.ManualDeleteRequested
                ? BackupRunStatus.ManualDeleted
                : BackupRunStatus.BackupExpiredDeleted;
            var reason = backup.Status == BackupRunStatus.ManualDeleteRequested ? "manual" : "retention";
            await scope.ServiceProvider.GetRequiredService<BackupCleanupService>().CleanupAsync(id, finalStatus, reason, cancellationToken);
        }, cancellationToken);
    }

    private async Task MarkExpiredAsync(ChoboDbContext db, IAuditService audit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var policies = await db.BackupPolicies
            .Where(x => !x.IsDeleted && (x.FullRetentionMinutes != null || x.IncrementalRetentionMinutes != null))
            .ToListAsync(cancellationToken);

        foreach (var policy in policies)
        {
            var successful = await db.Backups
                .Where(x => x.PolicyId == policy.Id && x.Status == BackupRunStatus.Succeeded)
                .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
                .ToListAsync(cancellationToken);
            var successfulIds = successful.Select(x => x.Id).ToList();
            var fullWorkIds = await FullWorkBackupIdsAsync(db, successfulIds, cancellationToken);
            var protectedGlobal = successful.Take(policy.MinBackupsToKeep).Select(x => x.Id).ToHashSet();
            var protectedFull = successful
                .Where(x => fullWorkIds.Contains(x.Id))
                .Take(policy.MinFullBackupsToKeep)
                .Select(x => x.Id)
                .ToHashSet();
            var expired = successful
                .Where(x => !protectedGlobal.Contains(x.Id) && !protectedFull.Contains(x.Id))
                .Where(x => !x.IsPinned)
                .Where(x =>
                {
                    var retentionMinutes = HasIncrementalWork(x) && !fullWorkIds.Contains(x.Id)
                        ? policy.IncrementalRetentionMinutes
                        : policy.FullRetentionMinutes;
                    return retentionMinutes is not null && (x.CompletedAt ?? x.CreatedAt) <= now.AddMinutes(-retentionMinutes.Value);
                })
                .ToList();

            foreach (var backup in expired)
            {
                if (fullWorkIds.Contains(backup.Id) && await HasLiveDependentIncrementalAsync(db, backup.Id, cancellationToken))
                {
                    continue;
                }

                backup.Status = BackupRunStatus.BackupExpiredDeleteStarted;
                backup.DeletionReason = "retention";
                backup.DeletionRequestedAt ??= now;
                backup.DeletionError = null;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync("backup-retention-delete-requested", AuditEntityType.Backup, backup.Id.ToString(), new
                {
                    policyId = policy.Id,
                    policy.FullRetentionMinutes,
                    policy.IncrementalRetentionMinutes,
                    policy.MinBackupsToKeep,
                    policy.MinFullBackupsToKeep
                });
            }
        }
    }

    private static bool HasIncrementalWork(BackupEntity backup) =>
        backup.BackupType == BackupType.Incremental;

    private static async Task<HashSet<Guid>> FullWorkBackupIdsAsync(ChoboDbContext db, IReadOnlyList<Guid> backupIds, CancellationToken cancellationToken)
    {
        var fullWorkIds = await db.Backups
            .Where(x => backupIds.Contains(x.Id) && x.BackupType == BackupType.Full)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        fullWorkIds.AddRange(await db.BackupTables
            .Where(x => backupIds.Contains(x.BackupId) && x.EffectiveBackupType == BackupType.Full)
            .Select(x => x.BackupId)
            .ToListAsync(cancellationToken));

        fullWorkIds.AddRange(await db.BackupTableShards
            .Where(x => x.BackupTable != null &&
                        backupIds.Contains(x.BackupTable.BackupId) &&
                        x.EffectiveBackupType == BackupType.Full)
            .Select(x => x.BackupTable!.BackupId)
            .ToListAsync(cancellationToken));

        return fullWorkIds.ToHashSet();
    }

    private static async Task<bool> HasLiveDependentIncrementalAsync(ChoboDbContext db, Guid fullBackupId, CancellationToken cancellationToken)
    {
        var deletedStatuses = FinalDeletedStatuses;
        return await db.Backups.AnyAsync(child =>
            !deletedStatuses.Contains(child.Status) &&
            (child.Tables.Any(table => table.ParentFullBackupTable != null && table.ParentFullBackupTable.BackupId == fullBackupId) ||
             child.Tables.Any(table => table.Shards.Any(shard => shard.ParentFullBackupTableShard != null && shard.ParentFullBackupTableShard.BackupTable!.BackupId == fullBackupId))),
            cancellationToken);
    }

    private static readonly BackupRunStatus[] FinalDeletedStatuses =
    [
        BackupRunStatus.ManualDeleted,
        BackupRunStatus.FailedBackupDeletedByGarbageCollector,
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
