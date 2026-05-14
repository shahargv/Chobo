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
            var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
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

    private async Task MarkExpiredAsync(ChoboDbContext db, AuditService audit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var policies = await db.BackupPolicies
            .Where(x => !x.IsDeleted && x.RetentionMinutes != null)
            .ToListAsync(cancellationToken);

        foreach (var policy in policies)
        {
            var cutoff = now.AddMinutes(-policy.RetentionMinutes!.Value);
            var successful = await db.Backups
                .Where(x => x.PolicyId == policy.Id && x.Status == BackupRunStatus.Succeeded)
                .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
                .ToListAsync(cancellationToken);
            var expired = successful
                .Skip(policy.MinBackupsToKeep)
                .Where(x => !x.IsPinned && (x.CompletedAt ?? x.CreatedAt) <= cutoff)
                .ToList();

            foreach (var backup in expired)
            {
                backup.Status = BackupRunStatus.BackupExpiredDeleteStarted;
                backup.DeletionReason = "retention";
                backup.DeletionRequestedAt ??= now;
                backup.DeletionError = null;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync("backup-retention-delete-requested", AuditEntityType.Backup, backup.Id.ToString(), new
                {
                    policyId = policy.Id,
                    policy.RetentionMinutes,
                    policy.MinBackupsToKeep,
                    cutoff
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
