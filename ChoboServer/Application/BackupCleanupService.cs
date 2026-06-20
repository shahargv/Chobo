using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class BackupCleanupService(
    ChoboDbContext db,
    IBackupStorageOperations storageOperations,
    IAuditService audit,
    Serilog.ILogger logger)
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupCleanupService>();

    public async Task<bool> CleanupAsync(Guid backupId, BackupRunStatus finalStatus, string reason, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.Target)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null)
        {
            return false;
        }
        if (backup.Status == finalStatus && backup.DeletedAt is not null)
        {
            return true;
        }

        backup.DeletionStartedAt = DateTimeOffset.UtcNow;
        backup.DeletionAttemptCount++;
        backup.DeletionError = null;
        backup.DeletionReason ??= reason;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("backup-cleanup-started", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, backup.DeletionAttemptCount });

        try
        {
            foreach (var directoryPath in BackupDirectories(backup))
            {
                await storageOperations.DeleteDirectoryAsync(backup.Target!, directoryPath, cancellationToken);
            }

            backup.Status = finalStatus;
            backup.DeletedAt = DateTimeOffset.UtcNow;
            backup.DeletionError = null;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("backup-cleanup-succeeded", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, backup.DeletionAttemptCount });
            await audit.RecordAsync("delete-completed", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, backup.DeletionAttemptCount, backup.DeletedAt });
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup cleanup failed for {BackupId}.", backup.Id);
            backup.DeletionError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("backup-cleanup-failed", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, error = ex.Message, backup.DeletionAttemptCount });
            return false;
        }
    }

    private static IEnumerable<string> BackupDirectories(BackupEntity backup)
    {
        foreach (var table in backup.Tables)
        {
            if (!string.IsNullOrWhiteSpace(table.S3Path))
            {
                yield return table.S3Path;
            }
        }
    }
}

