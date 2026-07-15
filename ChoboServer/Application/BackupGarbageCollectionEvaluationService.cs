using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class BackupGarbageCollectionEvaluationService(
    ChoboDbContext db,
    TimeProvider timeProvider)
{
    public async Task<BackupGarbageCollectionEvaluationDto?> EvaluateAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .AsNoTracking()
            .Include(x => x.Policy)
            .SingleOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null)
        {
            return null;
        }

        var evaluatedAt = timeProvider.GetUtcNow();
        var reasons = new List<BackupGarbageCollectionReasonDto>();

        if (FinalDeletedStatuses.Contains(backup.Status) || backup.DeletedAt is not null)
        {
            Add(BackupGarbageCollectionReason.BackupAlreadyDeleted,
                $"Backup {backup.Id} was already deleted{(backup.DeletedAt is null ? "." : $" at {backup.DeletedAt:O}.")}");
            return Result(false);
        }

        if (DeletionRequestedStatuses.Contains(backup.Status))
        {
            var detail = backup.DeletionError is null
                ? "It is waiting for, or currently undergoing, storage cleanup."
                : $"Its most recent cleanup attempt failed: {backup.DeletionError}";
            Add(BackupGarbageCollectionReason.DeletionAlreadyRequested,
                $"Deletion was already requested for backup {backup.Id}{(backup.DeletionRequestedAt is null ? "" : $" at {backup.DeletionRequestedAt:O}")}. {detail}");
            return Result(false);
        }

        if (backup.Status is BackupRunStatus.Queued or BackupRunStatus.Running)
        {
            Add(BackupGarbageCollectionReason.BackupInProgress,
                $"Backup {backup.Id} is {backup.Status} and cannot be garbage-collected while it is in progress.");
            return Result(false);
        }

        var policy = backup.Policy;
        if (backup.Status == BackupRunStatus.Failed &&
            policy?.FailedBackupRetentionMode == FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure)
        {
            Add(BackupGarbageCollectionReason.EligibleForDeletion,
                $"Backup {backup.Id} failed, and policy '{policy.Name}' is configured to garbage-collect failed backups.");
            return Result(true);
        }

        if (backup.Status is not (BackupRunStatus.Succeeded or BackupRunStatus.PartiallySucceeded or BackupRunStatus.Failed))
        {
            Add(BackupGarbageCollectionReason.BackupStatusNotEligible,
                $"Backup {backup.Id} has status {backup.Status}. Automatic retention deletion applies only to completed backups.");
            return Result(false);
        }

        if (policy is null)
        {
            Add(BackupGarbageCollectionReason.PolicyMissing,
                $"Backup {backup.Id} has no policy, so there is no automatic retention rule to delete it.");
            return Result(false);
        }

        if (policy.IsDeleted)
        {
            Add(BackupGarbageCollectionReason.PolicyDeleted,
                $"Policy '{policy.Name}' ({policy.Id}) is deleted, so its automatic retention rule is not active.");
            return Result(false);
        }

        // Failed backups kept by policy expire by age, but the policy mode explicitly
        // excludes them from minimum-backup counts. Partial backups remain usable and
        // therefore participate in the same safeguards as successful backups.
        var countable = await db.Backups
            .AsNoTracking()
            .Where(x => x.PolicyId == policy.Id &&
                (x.Status == BackupRunStatus.Succeeded || x.Status == BackupRunStatus.PartiallySucceeded))
            .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.BackupType })
            .ToListAsync(cancellationToken);
        var globalPosition = countable.FindIndex(x => x.Id == backup.Id) + 1;
        if (globalPosition > 0 && globalPosition <= policy.MinBackupsToKeep)
        {
            Add(BackupGarbageCollectionReason.ProtectedByMinimumBackupCount,
                $"Backup {backup.Id} is number {globalPosition} among the newest successful or partially successful backups for policy '{policy.Name}'. The policy protects the newest {policy.MinBackupsToKeep} backup(s).");
        }

        if (backup.BackupType == BackupType.Full)
        {
            var fullPosition = countable.Where(x => x.BackupType == BackupType.Full).ToList().FindIndex(x => x.Id == backup.Id) + 1;
            if (fullPosition > 0 && fullPosition <= policy.MinFullBackupsToKeep)
            {
                Add(BackupGarbageCollectionReason.ProtectedByMinimumFullBackupCount,
                    $"Backup {backup.Id} is number {fullPosition} among the newest successful or partially successful full backups for policy '{policy.Name}'. The policy protects the newest {policy.MinFullBackupsToKeep} full backup(s).");
            }
        }

        if (backup.IsPinned)
        {
            Add(BackupGarbageCollectionReason.BackupPinned,
                $"Backup {backup.Id} is pinned and automatic garbage collection never deletes pinned backups.");
        }

        var containsFullWork = backup.BackupType == BackupType.Full ||
            await db.BackupTables.AsNoTracking().AnyAsync(x => x.BackupId == backup.Id && x.EffectiveBackupType == BackupType.Full, cancellationToken) ||
            await db.BackupTableShards.AsNoTracking().AnyAsync(x => x.BackupTable != null && x.BackupTable.BackupId == backup.Id && x.EffectiveBackupType == BackupType.Full, cancellationToken);
        var retentionMinutes = backup.BackupType == BackupType.Incremental && !containsFullWork
            ? policy.IncrementalRetentionMinutes
            : policy.FullRetentionMinutes;
        if (retentionMinutes is null)
        {
            var kind = backup.BackupType == BackupType.Incremental && !containsFullWork ? "incremental" : "full";
            Add(BackupGarbageCollectionReason.RetentionNotConfigured,
                $"Policy '{policy.Name}' has no {kind}-backup retention time, so backup {backup.Id} does not expire automatically.");
        }
        else
        {
            var completedAt = backup.CompletedAt ?? backup.CreatedAt;
            var expiresAt = completedAt.AddMinutes(retentionMinutes.Value);
            if (expiresAt > evaluatedAt)
            {
                Add(BackupGarbageCollectionReason.RetentionPeriodNotElapsed,
                    $"Backup {backup.Id} expires at {expiresAt:O}; its {retentionMinutes.Value}-minute retention period has not elapsed yet.");
            }
        }

        if (containsFullWork)
        {
            var dependentIds = await LiveDependentBackupIdsAsync(backup.Id, cancellationToken);
            if (dependentIds.Count > 0)
            {
                Add(BackupGarbageCollectionReason.ActiveDependentBackups,
                    $"Backup {backup.Id} contains full backup data still required by {dependentIds.Count} active child backup(s): {string.Join(", ", dependentIds)}.",
                    dependentIds);
            }
        }

        if (reasons.Count == 0)
        {
            Add(BackupGarbageCollectionReason.EligibleForDeletion,
                $"Backup {backup.Id} is eligible for automatic deletion: its retention period has elapsed and no retention safeguard blocks it.");
            return Result(true);
        }

        return Result(false);

        void Add(BackupGarbageCollectionReason reason, string text, IReadOnlyList<Guid>? relatedBackupIds = null) =>
            reasons.Add(new BackupGarbageCollectionReasonDto(reason, text, relatedBackupIds ?? []));

        BackupGarbageCollectionEvaluationDto Result(bool eligible) =>
            new(backup.Id, eligible, string.Join(Environment.NewLine, reasons.Select(x => x.Text)), reasons, evaluatedAt);
    }

    private async Task<IReadOnlyList<Guid>> LiveDependentBackupIdsAsync(Guid fullBackupId, CancellationToken cancellationToken)
    {
        var nonBlockingStatuses = NonBlockingDependentStatuses;
        return await db.Backups
            .AsNoTracking()
            .Where(child => !nonBlockingStatuses.Contains(child.Status) &&
                (child.Tables.Any(table => table.ParentFullBackupTable != null && table.ParentFullBackupTable.BackupId == fullBackupId) ||
                 child.Tables.Any(table => table.Shards.Any(shard => shard.ParentFullBackupTableShard != null && shard.ParentFullBackupTableShard.BackupTable!.BackupId == fullBackupId))))
            .OrderBy(x => x.CompletedAt ?? x.CreatedAt)
            .Select(x => x.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private static readonly BackupRunStatus[] FinalDeletedStatuses =
    [
        BackupRunStatus.ManualDeleted,
        BackupRunStatus.FailedBackupDeletedByGarbageCollector,
        BackupRunStatus.BackupExpiredDeleted
    ];

    private static readonly BackupRunStatus[] DeletionRequestedStatuses =
    [
        BackupRunStatus.ManualDeleteRequested,
        BackupRunStatus.FailedBackupDeleteRequested,
        BackupRunStatus.BackupExpiredDeleteStarted
    ];

    // A child already committed to deletion must not keep its full parent alive for another GC interval.
    private static readonly BackupRunStatus[] NonBlockingDependentStatuses =
    [
        BackupRunStatus.ManualDeleteRequested,
        BackupRunStatus.ManualDeleted,
        BackupRunStatus.FailedBackupDeleteRequested,
        BackupRunStatus.FailedBackupDeletedByGarbageCollector,
        BackupRunStatus.BackupExpiredDeleteStarted,
        BackupRunStatus.BackupExpiredDeleted
    ];
}
