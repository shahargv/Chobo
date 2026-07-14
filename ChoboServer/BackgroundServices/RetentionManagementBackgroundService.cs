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
    IOptionsMonitor<RetentionManagementOptions> options,
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

            var interval = options.CurrentValue.Interval <= TimeSpan.Zero ? TimeSpan.FromHours(1) : options.CurrentValue.Interval;
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
            var evaluator = scope.ServiceProvider.GetRequiredService<BackupGarbageCollectionEvaluationService>();
            await MarkExpiredAsync(db, audit, evaluator, cancellationToken);
            pending = await db.Backups
                .Where(x => x.Status == BackupRunStatus.ManualDeleteRequested || x.Status == BackupRunStatus.BackupExpiredDeleteStarted)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        await ForEachAsync(pending, options.CurrentValue.MaxDop, async id =>
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

    private async Task MarkExpiredAsync(
        ChoboDbContext db,
        IAuditService audit,
        BackupGarbageCollectionEvaluationService evaluator,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var policies = await db.BackupPolicies
            .Where(x => !x.IsDeleted && (x.FullRetentionMinutes != null || x.IncrementalRetentionMinutes != null))
            .ToListAsync(cancellationToken);

        foreach (var policy in policies)
        {
            var successfulIds = await db.Backups
                .AsNoTracking()
                .Where(x => x.PolicyId == policy.Id && x.Status == BackupRunStatus.Succeeded)
                .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var backupId in successfulIds)
            {
                var evaluation = await evaluator.EvaluateAsync(backupId, cancellationToken);
                if (evaluation?.EligibleForDeletion != true)
                {
                    continue;
                }

                var backup = await db.Backups.SingleAsync(x => x.Id == backupId, cancellationToken);
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

