# Centralized Backup/Restore Queue Dispatch Design

## Goals

- Centralize backup/restore operation dispatch behind one configurable worker pool.
- Keep backup and restore operations as logical units of work. Operation runners remain responsible for status, progress, audit, cancellation, retries, and S3 metadata manifests.
- Preserve all existing queue semantics: forced rows, global position order, queue moves, stale row skipping, MaxDop layers, node limits, shard limits, and destination-path conflict checks.
- Improve readability of queue claim behavior by extracting claim policy from the broad queue application service.
- Keep SQLite queue access efficient for large queues.

## Non-goals

- Do not replace operation runners with generic shard workers in this change.
- Do not change public HTTP/CLI queue contracts.
- Do not change backup/restore manifest format, export format, or schema version.
- Do not move persistence/business logic into controllers or background-service adapters.

## Current Model

Manual, scheduled, resumed, and restore-created operations currently write whole operation ids to separate in-memory channels. Backup and restore runners then expand or verify persistent `BackupRestoreQueueItems` and execute shard work internally. The persistent queue is already the source for ordering and user-visible queue operations, but execution is split across backup, schema-only backup, and restore hosted services.

Risks in the current model:

- Operation dispatch is spread across several hosted services.
- The queue application service mixes user-facing queue APIs, queue row creation, claim ordering, capacity checks, and lease management.
- SQLite queue scans can become hot in large queues if claim loops repeatedly scan too much state.

## Proposed Flow

1. API/application services create or resume a backup/restore operation and enqueue a typed operation message:
   - `Kind`: backup or restore
   - `OperationId`
   - backup content mode
   - reason for log/debug context
2. One `BackupRestoreOperationDispatcherBackgroundService` starts `WorkerCount` consumers.
3. Each consumer reads one operation message, applies `MaxActiveQueueItems` gating, creates a scope, and invokes `BackupRunnerService.RunAsync` or `RestoreRunnerService.RunAsync`.
4. The runner remains the logical operation owner:
   - claims operation status from queued to running;
   - prepares tables and persistent queue rows;
   - executes shard workers internally using existing MaxDop logic;
   - updates table/shard progress;
   - writes S3 initial/checkpoint/final/failed manifests;
   - aggregates final status and failure reasons;
   - records audit entries.
5. Queue-row claiming inside the runner uses a centralized claim policy component shared by backup and restore.
6. If a runner cannot make progress because its queued rows are blocked by earlier rows, capacity, or another lease, it returns a non-terminal "deferred" outcome to the dispatcher. The dispatcher schedules the operation for retry after `PollInterval` instead of occupying a worker slot indefinitely.

## Operation Unit Of Work

Backup/restore runs stay durable operation units because they coordinate state beyond individual shard rows:

- backup manifests are operation-scoped and written initially, every configured `ManifestCheckpointShardInterval`, on success, and on failure;
- restore table preparation and final aggregation are operation/table scoped;
- audit records include operation-level lifecycle events;
- cancellation kills operation-related ClickHouse work and completes/removes queue rows;
- failure reasons aggregate table/shard errors for user-facing API and CLI output.

Therefore `WorkerCount` controls concurrent operation runners, not shard workers. Existing `MaxDop`, cluster maxdop, shard maxdop, node maxdop, and forced-row bypass behavior continue to control shard-level execution inside each operation.

## Application Service Responsibilities

- `BackupApplicationService`
  - Still validates and creates backup runs.
  - Still attempts post-commit queue preparation for manual backups to avoid permanent queued runs with no rows.
  - Enqueues one unified operation message instead of writing to a backup/schema-only channel.

- `RestoreApplicationService`
  - Still validates and creates restore runs and restore queue rows.
  - Enqueues one unified operation message instead of writing to a restore channel.

- `BackupSchedulerDispatcherBackgroundService`
  - Still creates scheduled backup runs and audit decisions.
  - Enqueues one unified backup operation message.

- `BackupRestoreResumeBackgroundService`
  - Still prunes inactive queue rows and resets incomplete claims for running operations.
  - Enqueues unified operation messages for queued/running backups and restores.

- `BackupRunnerService` and `RestoreRunnerService`
  - Stay operation lifecycle owners.
  - Continue to call queue services for row creation, claims, releases, completion, and cleanup.
  - Continue writing operation progress and manifests.

- `BackupRestoreQueueApplicationService`
  - Keeps user-facing list/move/force APIs and row creation/lease mutation.
  - Delegates ordering/capacity decision shape to a focused claim policy helper.

## Queue Claim Semantics To Preserve

- Candidate queue rows are ordered by `IsForced DESC`, then `Position ASC`.
- If a runner sees earlier queued work for another operation of the same queue kind, it must not jump ahead of it.
- Cross-kind order is global in this change: backups and restores share the same queue table and claim order, so a later restore cannot start ahead of an earlier active backup row, and a later backup cannot start ahead of an earlier active restore row.
- Queue moves change future claim order through persisted positions.
- Forced rows bypass cluster/shard/node capacity limits where they do today.
- Backup destination-path conflicts still block duplicate active backup writes to the same target path.
- Stale rows whose backing shard/table status is no longer active are skipped.
- In-memory leases still close race windows between SQLite reads and persisted claim updates.
- Started queue rows remain the only rows counted as active for `MaxActiveQueueItems`.

## SQLite Performance Strategy

- Keep claim transactions short: load ordered candidates in bounded keyset pages, evaluate one claim, update one row to `StartedAt`, commit. Keyset paging avoids increasingly expensive SQLite `Skip` scans while allowing stale rows beyond the first page to be skipped without hiding valid later work.
- Avoid whole-table scans in worker loops; claim queries must use indexes over active/queued state and order.
- Preserve DB-side filtering before API list limits.
- Use `PollInterval` when blocked by capacity or earlier queued work. Do not introduce tight spin loops.
- Maintain queue indexes in both `ChoboDbContext` and the v1 baseline migration:
  - `StartedAt, CompletedAt, IsForced, Position, CreatedAt` for global keyset candidate scans;
  - `Kind, OperationId` for operation cleanup/finalization;
  - `ClusterId, StartedAt, CompletedAt` for active-capacity checks;
  - `ClusterId, LogicalShardNumber, StartedAt, CompletedAt` for shard-capacity checks;
  - `ClusterId, NodeHost, NodePort, NodeUseTls, StartedAt, CompletedAt` for node-capacity checks;
  - unique `ShardId` for direct row lookup.
- Mirror key indexes in `DatabasePerformanceMaintenance` with `CREATE INDEX IF NOT EXISTS` so existing local databases get the performance benefits without a schema-version bump.

## Implementation Notes

- Add `WorkerCount` to `ChoboBackupRestoreOptions`.
- Replace `BackupExecutorBackgroundService`, `SchemaOnlyBackupExecutorBackgroundService`, and `RestoreExecutorBackgroundService` registrations with `BackupRestoreOperationDispatcherBackgroundService`.
- Replace the three public channels in `IBackupRestoreQueues` with a single typed channel and compatibility enqueue methods.
- Keep operation runner duplicate-execution guards (`ActiveBackupRuns`, `ActiveRestoreRuns`).
- Initial tests should focus on dispatch worker count, mixed operation dispatch, unified enqueue behavior, and existing queue-regression coverage.

## Accepted Tradeoffs

- Operation-level workers mean one large operation can internally run multiple shard workers. That is intentional because existing MaxDop layers already control shard parallelism, and operation-level ownership keeps progress and manifests correct.
- A unified operation queue centralizes dispatch but does not make the persistent queue the only wakeup source. Persistent queue rows remain durable ordering/progress state; the in-memory queue remains a wakeup mechanism and is repopulated on startup resume.
- `MaxActiveQueueItems` applies to dispatcher admission for all operation kinds. This extends the previous top-level backup-only gate, but aligns with a centralized worker pool and keeps the active shard queue bounded globally.
- Bounded candidate scans are allowed for SQLite performance, but the claim policy must explicitly distinguish "no queued work" from "work exists but is currently blocked" so the dispatcher can release the worker and retry later.

