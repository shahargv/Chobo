using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class DataRetentionBackgroundService(
    IServiceProvider services,
    IOptionsMonitor<ChoboDataRetentionOptions> options,
    TimeProvider timeProvider,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<DataRetentionBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Data retention purge failed.");
            }

            var interval = options.CurrentValue.Interval <= TimeSpan.Zero
                ? TimeSpan.FromHours(1)
                : options.CurrentValue.Interval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task<(int LogsDeleted, int AuditsDeleted, int BackupRecordsDeleted, int RestoreRecordsDeleted)> PurgeOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var logsDeleted = 0;
        var auditsDeleted = 0;
        var backupRecordsDeleted = 0;
        var restoreRecordsDeleted = 0;
        var retention = options.CurrentValue;

        if (retention.LogsBefore is not null)
        {
            var logs = scope.ServiceProvider.GetRequiredService<IApplicationLogStore>();
            logsDeleted = await logs.DeleteBeforeAsync(retention.LogsBefore.Value, cancellationToken);
        }

        if (retention.AuditsBefore is not null)
        {
            var audits = scope.ServiceProvider.GetRequiredService<IAuditStore>();
            auditsDeleted = await audits.DeleteBeforeAsync(retention.AuditsBefore.Value, cancellationToken);
        }

        if (retention.DeletedBackupRestoreRecordRetention > TimeSpan.Zero)
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var result = await PurgeDeletedBackupRestoreRecordsAsync(db, retention.DeletedBackupRestoreRecordRetention, cancellationToken);
            backupRecordsDeleted = result.BackupRecordsDeleted;
            restoreRecordsDeleted = result.RestoreRecordsDeleted;
        }

        if (retention.LogsBefore is not null || retention.AuditsBefore is not null || backupRecordsDeleted > 0 || restoreRecordsDeleted > 0)
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.RecordAsync("retention-purge", AuditEntityType.DataRetention, null, new
            {
                retention.LogsBefore,
                retention.AuditsBefore,
                retention.DeletedBackupRestoreRecordRetention,
                deletedBackupRestoreRecordCutoff = retention.DeletedBackupRestoreRecordRetention > TimeSpan.Zero
                    ? timeProvider.GetUtcNow().Subtract(retention.DeletedBackupRestoreRecordRetention)
                    : (DateTimeOffset?)null,
                logsDeleted,
                auditsDeleted,
                backupRecordsDeleted,
                restoreRecordsDeleted
            });
        }

        return (logsDeleted, auditsDeleted, backupRecordsDeleted, restoreRecordsDeleted);
    }

    private async Task<(int BackupRecordsDeleted, int RestoreRecordsDeleted)> PurgeDeletedBackupRestoreRecordsAsync(ChoboDbContext db, TimeSpan retention, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().Subtract(retention);
        var candidateIds = await db.Backups
            .Where(x => x.DeletedAt != null &&
                        x.DeletedAt < cutoff &&
                        x.DeletionError == null &&
                        DeletedBackupStatuses.Contains(x.Status))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            return (0, 0);
        }

        var candidateSet = candidateIds.ToHashSet();
        var parentBackupIdsWithRetainedTableDependents = await db.BackupTables
            .Where(x => x.ParentFullBackupTable != null &&
                        candidateIds.Contains(x.ParentFullBackupTable.BackupId) &&
                        !candidateIds.Contains(x.BackupId))
            .Select(x => x.ParentFullBackupTable!.BackupId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var parentBackupIdsWithRetainedShardDependents = await db.BackupTableShards
            .Where(x => x.ParentFullBackupTableShard != null &&
                        x.ParentFullBackupTableShard.BackupTable != null &&
                        x.BackupTable != null &&
                        candidateIds.Contains(x.ParentFullBackupTableShard.BackupTable.BackupId) &&
                        !candidateIds.Contains(x.BackupTable.BackupId))
            .Select(x => x.ParentFullBackupTableShard!.BackupTable!.BackupId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var protectedId in parentBackupIdsWithRetainedTableDependents.Concat(parentBackupIdsWithRetainedShardDependents))
        {
            candidateSet.Remove(protectedId);
        }

        var purgeBackupIds = candidateSet.ToList();
        if (purgeBackupIds.Count == 0)
        {
            return (0, 0);
        }

        var restoreIds = await db.Restores
            .Where(x => purgeBackupIds.Contains(x.BackupId))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var restoreRecordsDeleted = 0;
        if (restoreIds.Count > 0)
        {
            var restoreTableIds = await db.RestoreTables
                .Where(x => restoreIds.Contains(x.RestoreId))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            await db.RestoreTableShards
                .Where(x => restoreTableIds.Contains(x.RestoreTableId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.RestoreTables
                .Where(x => restoreIds.Contains(x.RestoreId))
                .ExecuteDeleteAsync(cancellationToken);
            restoreRecordsDeleted = await db.Restores
                .Where(x => restoreIds.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var backupTableIds = await db.BackupTables
            .Where(x => purgeBackupIds.Contains(x.BackupId))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        await db.BackupTableShards
            .Where(x => backupTableIds.Contains(x.BackupTableId))
            .ExecuteUpdateAsync(x => x.SetProperty(shard => shard.ParentFullBackupTableShardId, (Guid?)null), cancellationToken);
        await db.BackupTables
            .Where(x => purgeBackupIds.Contains(x.BackupId))
            .ExecuteUpdateAsync(x => x.SetProperty(table => table.ParentFullBackupTableId, (Guid?)null), cancellationToken);
        await db.BackupTableShards
            .Where(x => backupTableIds.Contains(x.BackupTableId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.BackupTables
            .Where(x => purgeBackupIds.Contains(x.BackupId))
            .ExecuteDeleteAsync(cancellationToken);
        var backupRecordsDeleted = await db.Backups
            .Where(x => purgeBackupIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);

        _logger.Information("Data retention hard-deleted {BackupRecordsDeleted} backup record(s) and {RestoreRecordsDeleted} restore record(s) older than {Cutoff:O}.", backupRecordsDeleted, restoreRecordsDeleted, cutoff);
        return (backupRecordsDeleted, restoreRecordsDeleted);
    }

    private static readonly BackupRunStatus[] DeletedBackupStatuses =
    [
        BackupRunStatus.Canceled,
        BackupRunStatus.ManualDeleted,
        BackupRunStatus.FailedBackupDeletedByGarbageCollector,
        BackupRunStatus.BackupExpiredDeleted
    ];
}
