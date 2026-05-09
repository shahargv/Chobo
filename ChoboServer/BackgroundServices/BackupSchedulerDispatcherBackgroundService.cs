using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class BackupSchedulerDispatcherBackgroundService(
    IServiceProvider services,
    IOptions<ChoboBackupRestoreOptions> options,
    BackupRestoreQueues queues,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupSchedulerDispatcherBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Backup schedule dispatch failed.");
            }

            var interval = options.Value.SchedulerInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : options.Value.SchedulerInterval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var schedules = await db.BackupSchedules
            .Include(x => x.Policy)
            .Where(x => x.IsEnabled && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        var cutoff = DateTimeOffset.UtcNow.Subtract(options.Value.SchedulerInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : options.Value.SchedulerInterval);
        foreach (var schedule in schedules)
        {
            var hasActive = await db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id && (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running), cancellationToken);
            if (hasActive)
            {
                await audit.RecordAsync("schedule-skip-active", "backup-schedule", schedule.Id.ToString(), new { reason = "active-backup-exists" });
                continue;
            }

            var hasRecent = await db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id && x.CreatedAt >= cutoff, cancellationToken);
            if (hasRecent)
            {
                continue;
            }

            if (schedule.BackupType != BackupType.Full)
            {
                await audit.RecordAsync("schedule-skip-unsupported", "backup-schedule", schedule.Id.ToString(), new { schedule.BackupType });
                continue;
            }
            if (schedule.Policy is null)
            {
                await audit.RecordAsync("schedule-skip-missing-policy", "backup-schedule", schedule.Id.ToString());
                continue;
            }

            var backup = new BackupEntity
            {
                TriggerType = BackupTriggerType.Scheduled,
                Status = BackupRunStatus.Queued,
                BackupType = schedule.BackupType,
                SourceClusterId = schedule.Policy.SourceClusterId,
                TargetId = schedule.Policy.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                RequestedByName = "system"
            };
            db.Backups.Add(backup);
            await db.SaveChangesAsync(cancellationToken);
            await queues.QueueBackupAsync(backup.Id, cancellationToken);
            await audit.RecordAsync("scheduled-backup-enqueued", "backup-schedule", schedule.Id.ToString(), new { backupId = backup.Id });
        }
    }
}
