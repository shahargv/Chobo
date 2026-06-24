# Backup/Restore Shard Queue Implementation Tasks

Status legend: `[ ]` pending, `[x]` complete.

1. [x] Create this ordered task tracker in `.codex/state`.
2. [x] Update contracts for queue DTOs, queue requests, and mandatory/throttle cluster fields.
3. [x] Update data model and schema for queue items and cluster throttle settings.
4. [x] Implement queue application service, reorder operations, force operation, validation, and audit.
5. [x] Implement random eligible replica selection per logical shard at queue dispatch time.
6. [x] Refactor backup shard scheduling to use the persisted queue dispatcher.
7. [x] Refactor restore scheduling to shard-level persisted queue dispatch.
8. [x] Update cluster create/update flows for required global MaxDop and default node/shard MaxDop of `1`.
9. [x] Add API controllers/endpoints and refresh OpenAPI/generated TypeScript types.
10. [x] Add CLI queue commands and cluster throttle options.
11. [x] Add GUI Queue page, navigation entry, controls, refresh behavior, and cluster throttle UI.
12. [x] Add unit/API/CLI/Web tests.
13. [x] Run bounded local unit/API/CLI/Web verification.
14. [x] Before full system tests, run subagent code reviews: general, performance, and edge-case.
15. [x] Add or adjust tests based on review findings.
16. [x] Add system tests, including single-instance ClickHouse regression coverage.
17. [x] Run selected/full bounded system tests only after reviews and follow-up fixes are complete.
18. [x] Update this task file after each completed task before moving to the next.

## Implementation Notes

- Queue controls apply only to queued shard-table rows.
- Force bypasses global, cluster, node, and shard MaxDop limits, but forced running rows count against later normal scheduling.
- Backup and restore share one combined queue.
- Dispatch chooses a random eligible replica/node from the logical shard at start time.
- Cluster/global `BackupRestoreMaxDop` is mandatory. `NodeMaxDopDefault` and `ShardMaxDopDefault` default to `1`.

- [x] Ensure the solution compiles after contract and API wiring changes.

## Review follow-up

- [x] Make queue claiming bounded and non-duplicating; avoid worker fanout beyond MaxDop.
- [x] Make workers retry blocked rows instead of exiting while queued work remains.
- [x] Enforce node MaxDop without falling back to a busy node.
- [x] Wire restore dispatch to persisted queue order and granular DOP semantics.
- [x] Preserve node/shard MaxDop fields in export/import and backup manifests.
- [x] Add pagination/status filtering for queue list views.
- [x] Update CLI docs and system test definitions for mandatory global MaxDop.
- [x] Add full execution tests for queue order, force, DOP, random replica, restore, and single-instance ClickHouse.


## Restore scheduling completion

- [x] Add restore queue claim logic with per-cluster MaxDop, NodeMaxDop, ShardMaxDop, and force bypass.
- [x] Refactor restore execution to shard-level persisted queue workers.
- [x] Preserve restore table preparation, schema-only completion, resume, cancellation, and failure aggregation.
- [x] Add restore queue scheduling tests for reorder, force, DOP, retry, random replica, and single-instance behavior.
- [x] Run required subagent reviews before full system tests and convert accepted findings into tasks.
- [x] Run bounded unit/API verification and selected system tests after review fixes.

## Restore scheduling review fixes

- [x] Exclude completed queue rows from claim windows and make queue row claiming conditional.
- [x] Reset incomplete restore queue claims on runner startup so resumed shards can be reclaimed.
- [x] Reduce restore table preparation loading to the target table instead of the full restore graph per table.
- [x] Complete backup/restore queue rows when operations are canceled so DOP is not consumed forever.
- [x] Prevent restore cancellation from being overwritten by late shard success/failure writes.
- [x] Move restore shard setup inside shard failure handling.
- [x] Make backup node saturation release and retry like restore.
- [x] Fix restore queue move audit entity type and queue list limit handling.
- [x] Add focused tests for review fixes.

Ignored by prior user scope: schema version/migration and old import/manifest compatibility follow-ups.
## Restore scheduling verification result

- [x] Focused unit/API tests passed: `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --filter "BackupRestoreExecutionTests|ChoboFoundationTests" --blame-hang --blame-hang-timeout 30s` (146 passed).
- [x] BackupRestoreSingleNode passed with test id codex-restore-single-20260624.
- [x] BackupRestoreCancellation passed with test id codex-restore-cancel-20260624.
- [x] LargeOnTimeBackupGc passed with test id codex-restore-large-20260624 in 1100.722 seconds.
## System test result

- [x] BackupRestoreSingleNode passed with test id codex-queue-single-20260624b.

- [x] LargeOnTimeBackupGc passed with test id codex-queue-large-20260624 in 1094.788 seconds.
## Remaining system test verification

- [x] BackupMetadataRecovery passed with test id codex-queue-backupmetadatarecovery-20260624c after fixing local-table backup/restore node selection.
- [x] BackupRestoreCoreScenarios passed with test id codex-queue-backuprestorecorescenarios-20260624.
- [x] BackupRestoreSharded passed with test id codex-queue-backuprestoresharded-20260624.
- [x] BackupRetentionCleanup passed with test id codex-queue-backupretentioncleanup-20260624.
- [x] BackupRetentionCleanupSharded passed with test id codex-queue-backupretentioncleanupsharded-20260624.
- [x] BootstrapCredentialPersistence passed with test id codex-queue-bootstrapcredentialpersistence-20260624.
- [x] BootstrapFirstSetup passed with test id codex-queue-bootstrapfirstsetup-20260624.
- [x] ChoboCrudSmoke passed with test id codex-queue-chobocrudsmoke-20260624b after updating the deleted-cluster negative test for mandatory cluster BackupRestoreMaxDop.
- [x] FailureScenario passed with test id codex-queue-failurescenario-20260624b after fixing backup queue claim recovery on server restart.
- [x] ImportExportRoundTrip passed with test id codex-queue-importexportroundtrip-20260624.
- [x] IncrementalBackupSharded passed with test id codex-queue-incrementalbackupsharded-20260624.
- [x] IncrementalBackupSingleNode passed with test id codex-queue-incrementalbackupsinglenode-20260624.
- [x] RestoreRedistributeTargetShardSubset passed with test id codex-queue-restoreredistributetargetshardsubset-20260624.
- [x] SchemaOnlyLargeSingleNode passed with test id codex-queue-schemaonlylargesinglenode-20260624.
- [x] SchemaVersionRejection passed with test id codex-queue-schemaversionrejection-20260624.
- [x] SmokeCreateTables passed with test id codex-queue-smokecreatetables-20260624.
- [x] SqliteSelfBackup passed with test id codex-queue-sqliteselfbackup-20260624.
- [x] Full local test project passed: `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s` (158 passed).
