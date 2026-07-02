# Centralized Backup/Restore Queue Dispatch Plan

## Summary

- Add a centralized backup/restore dispatcher with configurable operation workers, while preserving backup/restore runs as first-class logical units of work.
- Keep `BackupRunnerService` and `RestoreRunnerService` responsible for lifecycle, progress, manifests, audit, cancellation, retries, and final aggregation.
- Centralize queue-claim policy and make SQLite queue access efficient under large queue sizes.

## Initial Design Gate

- Write `.codex/state/centralized-queue-dispatch-design.md`.
- Run an application-design review subagent against the design note.
- Address findings in the design note or record accepted tradeoffs before implementation.

## Key Changes

- Add `WorkerCount` to `ChoboBackupRestoreOptions`; normalize `<= 0` to `1`.
- Replace separate backup/schema-only/restore executor hosted services with one `BackupRestoreOperationDispatcherBackgroundService`.
- Use one bounded typed operation queue:
  - `Kind`: `Backup` or `Restore`
  - `OperationId`
  - backup `ContentMode`
  - enqueue reason for logs/tests
- Preserve backup/restore operation boundaries:
  - operation workers claim and run whole backup/restore operations;
  - runners continue to own progress state, table/shard status aggregation, S3 manifest checkpoints every configured `ManifestCheckpointShardInterval`, final/failed manifest writes, and operation-level audit;
  - shard execution remains inside the operation runner so progress and finalization stay coherent.
- Extract claim selection into a clear queue-claim policy component shared by backup and restore paths, preserving:
  - forced rows first, then global `Position` across backup and restore rows;
  - earlier active queue rows blocking later operation runners regardless of operation kind;
  - stale/terminal row skipping;
  - global/cluster, shard, node, and destination-path limits;
  - forced-row bypass semantics;
  - in-memory leases plus persisted claim checks.
- Keep public HTTP/CLI queue APIs and DTOs unchanged.

## SQLite Queue Performance

- Review and add/adjust SQLite indexes for hot queue paths:
  - queued claim scan: `Kind`, `StartedAt`, `CompletedAt`, `IsForced`, `Position`;
  - operation rows: `Kind`, `OperationId`;
  - active capacity checks: `ClusterId`, `StartedAt`, `CompletedAt`;
  - shard lookup: `Kind`, `ShardId`.
- Avoid full queue scans in worker loops; claim ordered candidates in small pages and short-circuit once earlier active blocking work is detected.
- Keep transactions short: select candidates, validate capacity, mark one row started, commit.
- Do not poll aggressively when work is blocked by capacity; use existing `PollInterval` and consider bounded backoff only if tests show SQLite pressure.
- Ensure list/status endpoints keep applying DB-side filters before limits.

## Detailed Task List

- [x] Create `.codex/state/centralized-queue-dispatch-plan.md` with this plan and checklist.
- [x] Create `.codex/state/centralized-queue-dispatch-design.md`.
- [x] Run design-review subagent and record outcomes.
- [x] Add `WorkerCount` option and config default.
- [x] Introduce unified operation work item and queue abstraction.
- [x] Update manual backup, scheduled backup, restore initiation, and startup resume to enqueue unified operation messages.
- [x] Implement centralized dispatcher with exactly `WorkerCount` consumers.
- [x] Preserve `MaxActiveQueueItems` gating before starting another operation runner.
- [x] Remove old executor hosted-service registrations after the new dispatcher is wired.
- [x] Extract queue claim policy from `BackupRestoreQueueApplicationService`.
- [x] Add/verify SQLite indexes for queue claim and capacity paths.
- [x] Update tests/fixtures that inspect old channels to use the unified queue abstraction.
- [x] Run bounded unit tests.
- [x] Run selected system tests with explicit timeouts if feasible.

## Design Review Outcomes

- Worker-slot deadlock risk: addressed by requiring runners to return a deferred outcome when queued rows exist but cannot currently be claimed, letting the dispatcher retry after `PollInterval` instead of holding the worker.
- Global ordering ambiguity: resolved after final bug-hunter review by enforcing one global `IsForced DESC, Position ASC` order across backup and restore claim paths.
- `MaxActiveQueueItems` behavior: accepted as globally applied at dispatcher admission for all operation kinds; add regression coverage.
- Bounded candidate windows: addressed with paged candidate reads using `BackupRestoreQueueClaimPolicy.CandidateWindow`, explicit blocked/deferred claim results, and retry behavior after `PollInterval`.
- Restore resume capacity deadlock: addressed by resetting incomplete restore claims during startup resume before enqueueing the restore operation.
- Active import race: addressed by serializing data/config import against operation creation with `BackupRestoreOperationGate`, rejecting data import while local operations are queued/running, and clearing persisted queue rows only after the guard passes.
## Test Plan

- Unit/integration tests:
  - `WorkerCount` option binding and normalization.
  - dispatcher never runs more than configured operation workers.
  - mixed backup/restore queue dispatch works.
  - schema-only backups still finish without data shard work.
  - operation progress and S3 checkpoint manifests still update every configured shard interval.
  - one operation failure does not kill dispatcher workers.
  - `MaxActiveQueueItems` still counts only started incomplete queue rows.
- Queue regression tests:
  - forced rows before position.
  - later operation cannot jump earlier active queue rows.
  - cluster/shard/node/destination limits still hold.
  - forced bypass behavior remains intentional.
  - stale rows are skipped.
  - retry leases still block premature reclaim.
- System tests:
  - `BackupRestoreSingleNode`
  - `BackupRestoreSharded`
  - `BackupRestoreCoreScenarios`
  - `BackupRestoreCancellation`
  - `BackupRestoreFailureHandling`
  - `BackupRestoreReplicatedNodeDown`
  - `IncrementalBackupSingleNode`
  - `IncrementalBackupSharded`
  - `SchemaOnlyLargeSingleNode`
  - `LargeMetadataResponsiveness`
- Final verification:
  - `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s`
  - selected `TestingSuite\TestManager.ps1` runs with explicit `-TestId`, `-GlobalTimeoutSeconds`, `-TestTimeoutSeconds`, and final passing run with `-CleanTestResults`.

## Review Gates

- Initial application-design subagent reviews the design note before implementation starts.
- Performance subagent checks SQLite query plans, indexes, lock contention, worker polling, and large queue behavior.
- Design subagent checks operation boundaries, runner ownership, onion architecture, typed options, and audit invariants.
- Plan-match subagent verifies implementation and `.codex/state` match this plan.
- Weird bug hunter targets races, duplicate execution, force, cancellation timing, stale rows, resume, schema-only backups, restore prep failures, and mixed queue ordering.

## Assumptions

- `WorkerCount` controls concurrent backup/restore operation runners.
- Existing `MaxDop` and cluster/shard/node settings continue to control shard-level parallelism inside each operation.
- Backup/restore operations remain the durable progress and manifest checkpoint units.



## Final Implementation Notes

- Implemented `BackupRestoreOperationDispatcherBackgroundService` with configurable `ChoboBackupRestoreOptions.WorkerCount` and one unified operation channel in `BackupRestoreQueues`.
- Removed backup/schema-only/restore executor hosted services and registered the centralized dispatcher.
- Kept `BackupRunnerService` and `RestoreRunnerService` as operation lifecycle owners, including audit, manifest checkpoints, cancellation, retries, progress, and final aggregation.
- Added `BackupRestoreQueueClaimPolicy` and global paged queue ordering across backup/restore rows.
- Added SQLite indexes for global keyset queued scans, active capacity checks, operation cleanup, node/shard limits, and backup storage-path checks; mirrored them in baseline schema and performance maintenance.
- Added import compatibility safeguards: previous-version config/data payloads still import, imported in-flight operations are normalized to failed, stale queue rows are cleared during data import, active local operations block import, and import is serialized against operation creation.

## Final Verification Results

- `dotnet build ChoboServer\ChoboServer.csproj -v minimal`: passed; existing `Microsoft.OpenApi` NU1903 warning only.
- `dotnet build Chobo.Tests\Chobo.Tests.csproj -v minimal`: passed; existing `Microsoft.OpenApi` NU1903 warning only.
- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --no-build --blame-hang --blame-hang-timeout 30s`: passed, 256 tests.
- Focused new regression filter for global ordering, restore resume reset, active import guard, and atomic max-active queue cap: passed, 4 tests.
- `TestingSuite\TestManager.ps1` system tests passed:
  - `BackupRestoreSingleNode`, test id `codex-queue-dispatch-single-20260702-005`.
  - `BackupRestoreSharded`, test id `codex-queue-dispatch-sharded-20260702-005`.
  - `BackupRestoreCancellation`, test id `codex-queue-dispatch-cancel-20260702-005`.
  - `FailureScenario`, test id `codex-queue-dispatch-failure-20260702-005`.
  - `ImportExportRoundTrip`, test id `codex-queue-dispatch-importexport-20260702-005`.
- Full `TestingSuite\TestManager.ps1` run-all validation:
  - `codex-full-system-20260702-queue`: 21/22 passed at default run-all concurrency; `BackupRestoreReplicatedNodeDown` timed out waiting for restore while the CLI request hit the configured 100 second HTTP timeout.
  - `codex-rerun-replicated-node-down-20260702`: isolated rerun of `BackupRestoreReplicatedNodeDown` passed in 20.553 seconds.
  - `codex-full-system-sequential-20260702-queue`: full run-all with `-RunAllConcurrency 1` passed, 22/22 tests, 1957.594 seconds.
- Performance validation:
  - `codex-performance-largemetadata-20260702`: `LargeMetadataResponsiveness` passed, 10.623 seconds total, 10.421 seconds test duration.