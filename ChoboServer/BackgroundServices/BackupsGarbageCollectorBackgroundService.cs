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
            var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
            await MarkFailedBackupsAsync(db, audit, cancellationToken);
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

    private async Task MarkFailedBackupsAsync(ChoboDbContext db, AuditService audit, CancellationToken cancellationToken)
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
            backup.Status = BackupRunStatus.FailedBackupDeleteRequested;
            backup.DeletionReason = "failed-backup-garbage-collector";
            backup.DeletionRequestedAt ??= now;
            backup.DeletionError = null;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("failed-backup-garbage-collection-requested", AuditEntityType.Backup, backup.Id.ToString(), new { backup.PolicyId, backup.Status });
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
