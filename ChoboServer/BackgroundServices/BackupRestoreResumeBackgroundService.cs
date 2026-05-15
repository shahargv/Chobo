using Chobo.Contracts;
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
            var backups = await db.Backups
                .Where(x => x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running)
                .Select(x => x.Id)
                .ToListAsync(stoppingToken);
            foreach (var id in backups)
            {
                await queues.QueueBackupAsync(id, stoppingToken);
                await audit.RecordAsync("resumed", AuditEntityType.Backup, id.ToString(), new { reason = "server-startup" });
            }

            var restores = await db.Restores
                .Where(x => x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running)
                .Select(x => x.Id)
                .ToListAsync(stoppingToken);
            foreach (var id in restores)
            {
                await queues.QueueRestoreAsync(id, stoppingToken);
                await audit.RecordAsync("resumed", AuditEntityType.Restore, id.ToString(), new { reason = "server-startup" });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup/restore resume failed.");
        }
    }
}
