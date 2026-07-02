using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.BackgroundServices;

public sealed class BackupRestoreResumeBackgroundService(
    IServiceProvider services,
    IBackupRestoreQueues queues,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupRestoreResumeBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            var queueService = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            var pruned = await queueService.RemoveInactiveOperationItemsAsync("server-startup", stoppingToken);
            if (pruned > 0)
            {
                _logger.Information("Backup/restore startup pruned {QueueItemCount} inactive queue row(s).", pruned);
            }

            var backups = await db.Backups
                .Where(x => x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running)
                .Select(x => new { x.Id, x.ContentMode, x.Status })
                .ToListAsync(stoppingToken);
            foreach (var backup in backups)
            {
                if (backup.Status == BackupRunStatus.Running)
                {
                    await queueService.ResetIncompleteBackupClaimsAsync(backup.Id, stoppingToken);
                }

                await queues.QueueBackupAsync(backup.Id, backup.ContentMode, stoppingToken);
                await audit.RecordAsync("resumed", AuditEntityType.Backup, backup.Id.ToString(), new { reason = "server-startup" });
            }

            var restores = await db.Restores
                .Where(x => x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running)
                .Select(x => new { x.Id, x.Status })
                .ToListAsync(stoppingToken);
            foreach (var restore in restores)
            {
                if (restore.Status == RestoreRunStatus.Running)
                {
                    await queueService.ResetIncompleteRestoreClaimsAsync(restore.Id, stoppingToken);
                }

                await queues.QueueRestoreAsync(restore.Id, stoppingToken);
                await audit.RecordAsync("resumed", AuditEntityType.Restore, restore.Id.ToString(), new { reason = "server-startup" });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup/restore resume failed.");
        }
    }
}

