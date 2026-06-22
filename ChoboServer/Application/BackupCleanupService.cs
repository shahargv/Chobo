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
        _logger.Information("Backup cleanup loading backup {BackupId}. FinalStatus={FinalStatus}, reason={Reason}.", backupId, finalStatus, reason);
        var backup = await db.Backups
            .Include(x => x.Target)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null)
        {
            _logger.Warning("Backup cleanup skipped because backup {BackupId} was not found.", backupId);
            return false;
        }
        if (backup.Status == finalStatus && backup.DeletedAt is not null)
        {
            _logger.Information("Backup cleanup skipped for backup {BackupId} because it is already deleted with final status {FinalStatus}.", backup.Id, finalStatus);
            return true;
        }

        backup.DeletionStartedAt = DateTimeOffset.UtcNow;
        backup.DeletionAttemptCount++;
        backup.DeletionError = null;
        backup.DeletionReason ??= reason;
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Backup cleanup started for backup {BackupId}. Attempt={DeletionAttemptCount}, currentStatus={CurrentStatus}, tableCount={TableCount}.", backup.Id, backup.DeletionAttemptCount, backup.Status, backup.Tables.Count);
        await audit.RecordAsync("backup-cleanup-started", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, backup.DeletionAttemptCount });

        try
        {
            var deletedPathCount = 0;
            var directories = BackupDirectories(backup).ToList();
            _logger.Information("Backup cleanup storage deletion phase started for backup {BackupId}. DirectoryCount={DirectoryCount}, hasTarget={HasTarget}.", backup.Id, directories.Count, backup.Target is not null);
            foreach (var directoryPath in directories)
            {
                if (backup.Target is not null)
                {
                    _logger.Information("Backup cleanup deleting storage directory for backup {BackupId}: {DirectoryPath}.", backup.Id, directoryPath);
                    await storageOperations.DeleteDirectoryAsync(backup.Target, directoryPath, cancellationToken);
                    deletedPathCount++;
                }
            }
            _logger.Information("Backup cleanup storage deletion phase completed for backup {BackupId}. DeletedPathCount={DeletedPathCount}.", backup.Id, deletedPathCount);

            _logger.Information("Backup cleanup schema cleanup phase started for backup {BackupId}.", backup.Id);
            var schemaCleanup = await CleanupSchemaDefinitionsAsync(backup, cancellationToken);
            _logger.Information("Backup cleanup schema cleanup phase completed for backup {BackupId}. SchemaReferencesCleared={SchemaReferencesCleared}, SchemaDefinitionsDeleted={SchemaDefinitionsDeleted}.", backup.Id, schemaCleanup.SchemaReferencesCleared, schemaCleanup.SchemaDefinitionsDeleted);

            backup.Status = finalStatus;
            backup.DeletedAt = DateTimeOffset.UtcNow;
            backup.DeletionError = null;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Backup cleanup completed for backup {BackupId}. FinalStatus={FinalStatus}, deletedAt={DeletedAt}.", backup.Id, finalStatus, backup.DeletedAt);
            await audit.RecordAsync("backup-cleanup-succeeded", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, backup.DeletionAttemptCount, deletedPathCount, schemaCleanup.SchemaReferencesCleared, schemaCleanup.SchemaDefinitionsDeleted });
            await audit.RecordAsync("delete-completed", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, backup.DeletionAttemptCount, backup.DeletedAt, deletedPathCount, schemaCleanup.SchemaReferencesCleared, schemaCleanup.SchemaDefinitionsDeleted });
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup cleanup failed for backup {BackupId}. Attempt={DeletionAttemptCount}, finalStatus={FinalStatus}, reason={Reason}.", backup.Id, backup.DeletionAttemptCount, finalStatus, reason);
            backup.DeletionError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("backup-cleanup-failed", AuditEntityType.Backup, backup.Id.ToString(), new { reason, finalStatus, error = ex.Message, backup.DeletionAttemptCount });
            return false;
        }
    }

    private async Task<(int SchemaReferencesCleared, int SchemaDefinitionsDeleted)> CleanupSchemaDefinitionsAsync(BackupEntity backup, CancellationToken cancellationToken)
    {
        var schemaIds = backup.Tables
            .Where(x => x.SchemaDefinitionId is not null)
            .Select(x => x.SchemaDefinitionId!.Value)
            .Distinct()
            .ToList();
        if (schemaIds.Count == 0)
        {
            _logger.Information("Backup cleanup found no schema definitions to clean for backup {BackupId}.", backup.Id);
            return (0, 0);
        }

        foreach (var table in backup.Tables.Where(x => x.SchemaDefinitionId is not null))
        {
            table.SchemaDefinitionId = null;
            table.SchemaDefinition = null;
        }
        await db.SaveChangesAsync(cancellationToken);

        var stillReferenced = await db.BackupTables
            .Where(x => x.SchemaDefinitionId != null && schemaIds.Contains(x.SchemaDefinitionId.Value))
            .Select(x => x.SchemaDefinitionId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var deletable = schemaIds.Except(stillReferenced).ToList();
        var deleted = 0;
        if (deletable.Count > 0)
        {
            deleted = await db.SchemaDefinitions.Where(x => deletable.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        return (schemaIds.Count, deleted);
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




