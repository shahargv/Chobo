using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class BackupSchedulerDispatcherBackgroundService(
    IServiceProvider services,
    IOptions<ChoboBackupRestoreOptions> options,
    IBackupRestoreQueues queues,
    Serilog.ILogger logger,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupSchedulerDispatcherBackgroundService>();
    private static readonly string[] ScheduleDecisionActions =
    [
        "scheduled-backup-enqueued",
        "scheduled-backup-missed",
        "schedule-skip-active",
        "schedule-skip-active-policy",
        "schedule-skip-missing-policy"
    ];

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
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var schedules = await db.BackupSchedules
            .Include(x => x.Policy)
            .Where(x => x.IsEnabled && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var schedulerInterval = options.Value.SchedulerInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : options.Value.SchedulerInterval;
        var defaultMissedRunGracePeriod = options.Value.SchedulerMissedRunGracePeriod <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : options.Value.SchedulerMissedRunGracePeriod;
        var recentCutoff = now.Subtract(schedulerInterval);
        foreach (var schedule in schedules)
        {
            var missedRunGracePeriod = schedule.MissedRunGracePeriod is { } scheduleGracePeriod && scheduleGracePeriod > TimeSpan.Zero
                ? scheduleGracePeriod
                : defaultMissedRunGracePeriod;

            if (!TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZoneId, out var timeZone))
            {
                await audit.RecordAsync("schedule-skip-invalid-timezone", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new { schedule.TimeZoneId });
                continue;
            }

            var lastBackupCreatedAt = await db.Backups
                .Where(x => x.ScheduleId == schedule.Id &&
                            x.Status != BackupRunStatus.Queued &&
                            x.Status != BackupRunStatus.Running)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => (DateTimeOffset?)x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            var backupScheduleAuditEntityType = AuditEntityTypes.ToStorageValue(AuditEntityType.BackupSchedule);
            var lastDecisionAt = await db.AuditEntries
                .Where(x => x.EntityType == backupScheduleAuditEntityType &&
                            x.EntityId == schedule.Id.ToString() &&
                            ScheduleDecisionActions.Contains(x.Action))
                .OrderByDescending(x => x.Timestamp)
                .Select(x => (DateTimeOffset?)x.Timestamp)
                .FirstOrDefaultAsync(cancellationToken);
            var windowStart = Max(schedule.CreatedAt, lastBackupCreatedAt, lastDecisionAt);

            DateTimeOffset? latestOccurrence;
            try
            {
                latestOccurrence = QuartzCronProjection.GetLatestOccurrence(schedule.CronExpression, timeZone, windowStart, now);
            }
            catch (FormatException ex)
            {
                await audit.RecordAsync("schedule-skip-invalid-cron", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new { schedule.CronExpression, error = ex.Message });
                _logger.Warning(
                    ex,
                    "Skipping schedule {ScheduleId} ({ScheduleName}) because cron expression {CronExpression} is invalid.",
                    schedule.Id,
                    schedule.Name,
                    schedule.CronExpression);
                continue;
            }

            if (latestOccurrence is null)
            {
                continue;
            }

            var lateness = now - latestOccurrence.Value;
            if (lateness > missedRunGracePeriod)
            {
                await audit.RecordAsync("scheduled-backup-missed", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new
                {
                    plannedRunAt = latestOccurrence,
                    detectedAt = now,
                    latenessSeconds = Math.Round(lateness.TotalSeconds, 3),
                    gracePeriodSeconds = Math.Round(missedRunGracePeriod.TotalSeconds, 3)
                });
                _logger.Warning(
                    "Scheduled backup missed for schedule {ScheduleId}; planned run at {PlannedRunAt:O}, detected at {DetectedAt:O}, lateness {LatenessSeconds:n3}s exceeded grace period {GracePeriodSeconds:n3}s.",
                    schedule.Id,
                    latestOccurrence,
                    now,
                    lateness.TotalSeconds,
                    missedRunGracePeriod.TotalSeconds);
                continue;
            }

            var hasActive = await db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id && (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running), cancellationToken);
            if (hasActive)
            {
                await audit.RecordAsync("schedule-skip-active", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new { reason = "active-backup-exists", plannedRunAt = latestOccurrence });
                continue;
            }

            var hasActivePolicyBackup = await db.Backups.AnyAsync(x => x.PolicyId == schedule.PolicyId && (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running), cancellationToken);
            if (hasActivePolicyBackup)
            {
                await audit.RecordAsync("schedule-skip-active-policy", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new { reason = "active-policy-backup-exists", schedule.PolicyId, plannedRunAt = latestOccurrence });
                continue;
            }

            var hasRecent = await db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id && x.CreatedAt >= recentCutoff, cancellationToken);
            if (hasRecent)
            {
                continue;
            }

            if (schedule.Policy is null)
            {
                await audit.RecordAsync("schedule-skip-missing-policy", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new { plannedRunAt = latestOccurrence });
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
            await audit.RecordAsync("scheduled-backup-enqueued", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new { operationId = backup.Id, backupId = backup.Id, plannedRunAt = latestOccurrence });
        }
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset? second, DateTimeOffset? third)
    {
        var result = first;
        if (second is not null && second.Value > result)
        {
            result = second.Value;
        }
        if (third is not null && third.Value > result)
        {
            result = third.Value;
        }

        return result;
    }
}

