# Backup Lifecycle Management

Chobo manages backup lifecycle through policy retention, pinning, manual deletion, and failed-backup garbage collection.

Lifecycle actions are audited. Deletion removes backup objects from the configured storage target and updates the backup run so operators can see what happened.

## Policy Retention

Retention is configured on a backup policy:

```powershell
ChoboCli policies add --name nightly-prod --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json --full-retention-minutes 43200 --incremental-retention-minutes 10080 --min-backups-to-keep 7 --min-full-backups-to-keep 2
```

`--full-retention-minutes` and `--incremental-retention-minutes` set separate age thresholds. Chobo compares them to each successful backup's `endedAt` timestamp, falling back to `createdAt` when needed.

`--min-backups-to-keep` keeps the newest successful backups for the policy even if they are older than the retention threshold.

`--min-full-backups-to-keep` keeps the newest full backups for the policy so incremental backups retain usable parents.

Full backups with dependent incrementals are not deleted until those incrementals are deleted. Pinned incrementals block non-force deletion of their parent full backup.

Only `Succeeded` backups are expired by policy retention. Failed and partially succeeded backups are handled separately by failed-backup retention mode.

## Pinning

Pinned backups are not expired by policy retention.

Pin a backup:

```powershell
ChoboCli backups pin --id <backup-id>
```

Unpin a backup:

```powershell
ChoboCli backups unpin --id <backup-id>
```

Pinned backups can still be manually deleted with force:

```powershell
ChoboCli backups delete --id <backup-id> --force
```

Without `--force`, Chobo rejects manual deletion of a pinned backup.

## Manual Deletion

Request deletion:

```powershell
ChoboCli backups delete --id <backup-id>
```

Queued and running backups cannot be deleted.

The delete command changes the run to `ManualDeleteRequested`. The retention management background service performs the actual S3 object deletion and then marks the run `ManualDeleted`.

If deletion fails, Chobo stores `deletionError`, increments `deletionAttemptCount`, and retries on a later lifecycle pass.

## Failed Backup Garbage Collection

Failed-backup retention mode is configured on the policy:

```powershell
ChoboCli policies update --id <policy-id> --name nightly-prod --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json --failed-backup-retention-mode DeleteByGarbageCollectorAfterFailure
```

Modes:

- `KeepAndExcludeFromMinBackupsToKeep`: keep failed and partially succeeded backups. This is the default.
- `DeleteByGarbageCollectorAfterFailure`: mark failed and partially succeeded backups for S3 cleanup.

When garbage collection is enabled, backups in `Failed` or `PartiallySucceeded` become `FailedBackupDeleteRequested`, and then `FailedBackupDeletedByGarbageCollector` after object deletion succeeds.

## Background Services

Retention management:

```json
{
  "Chobo": {
    "RetentionManagement": {
      "Interval": "01:00:00",
      "MaxDop": 2
    }
  }
}
```

This service:

- Marks expired successful backups as `BackupExpiredDeleteStarted`.
- Processes `BackupExpiredDeleteStarted` backups.
- Processes `ManualDeleteRequested` backups.

Failed-backup garbage collector:

```json
{
  "Chobo": {
    "BackupsGarbageCollector": {
      "Interval": "01:00:00",
      "MaxDop": 2
    }
  }
}
```

This service:

- Marks failed or partially succeeded backups for cleanup when their policy requests it.
- Processes `FailedBackupDeleteRequested` backups.

## Deletion Statuses

Lifecycle statuses:

- `ManualDeleteRequested`: an operator requested deletion.
- `ManualDeleted`: manual deletion completed.
- `BackupExpiredDeleteStarted`: policy retention marked the backup for deletion.
- `BackupExpiredDeleted`: policy retention deletion completed.
- `FailedBackupDeleteRequested`: failed-backup garbage collection marked the backup for deletion.
- `FailedBackupDeletedByGarbageCollector`: failed-backup cleanup completed.

Deletion metadata in `backups show`:

- `isPinned`
- `pinnedAt`
- `pinnedByUserId`
- `pinnedByName`
- `deletionReason`
- `deletionRequestedAt`
- `deletionStartedAt`
- `deletedAt`
- `deletionError`
- `deletionAttemptCount`

## S3 Deletion Behavior

Chobo deletes the S3 prefixes recorded on the backup tables and shards. If the target has a path prefix, Chobo prepends it before listing and deleting objects.

For S3-compatible deletion, ChoboServer needs access to the target endpoint and the stored S3 credentials. Deletion requests are signed with AWS Signature Version 4.

If a backup has shard paths, Chobo deletes each shard path. If a backup table has no shard paths, Chobo deletes the table path. Schema-only backups have no S3 paths, so cleanup skips object deletion for them.

Cleanup never removes the backup run record itself. For every deleted or expired backup, including schema+data and schema-only backups, Chobo clears the backup table schema references and deletes schema definition rows that are no longer referenced by any retained backup. Cleanup audit details include deleted S3 path count, cleared schema reference count, and deleted schema definition count.

## Auditing

Lifecycle actions write audit records. Useful action names:

- `pin`
- `unpin`
- `delete-requested`
- `backup-retention-delete-requested`
- `failed-backup-garbage-collection-requested`
- `backup-cleanup-started`
- `backup-cleanup-succeeded`
- `backup-cleanup-failed`

Inspect audit records:

```powershell
ChoboCli audit show --last 200
```

## Common Operations

List backups by status:

```powershell
ChoboCli backups list --status BackupExpiredDeleteStarted
ChoboCli backups list --status FailedBackupDeleteRequested
```

Check deletion progress:

```powershell
ChoboCli backups show --id <backup-id>
```

Review cleanup errors:

```powershell
ChoboCli backups list --status ManualDeleteRequested
ChoboCli logs show --last 500
ChoboCli audit show --last 200
```

Temporarily protect an important backup:

```powershell
ChoboCli backups pin --id <backup-id>
```

Release it back to normal retention:

```powershell
ChoboCli backups unpin --id <backup-id>
```


