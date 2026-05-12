# Multiple-Shard Backup and Restore State

## Goal
Implement sharded ClickHouse backup/restore with one selected representative node per shard, per-shard metadata, partial success reporting, restore layout controls, CLI/API visibility, audit records, and system tests under `TestingSuite/Tests/BackupRestoreSharded`.

## Checklist
- [x] Metadata/contracts/schema version
- [x] Topology discovery and node-scoped ClickHouse operations
- [x] Sharded backup preparation/execution/resume/audit
- [x] Sharded restore planning/execution/resume/audit
- [x] CLI/dashboard/metrics visibility
- [x] Unit tests
- [x] System tests
- [x] Final verification

## Implementation Notes
- Default restore layout is preserve when source and target shard counts match; otherwise callers must choose `single-node` or `redistribute`.
- `ON CLUSTER` must not be used for backup/restore execution.
- ReplicatedMergeTree is in scope; Chobo backs up one replica per shard.
- Old runs should remain understandable after topology changes because shard task rows store selected node details.

## Test Status
- `dotnet build Chobo.sln -v minimal -m:1 --no-restore /p:UseAppHost=false -clp:ErrorsOnly` passed.
- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --no-build --blame-hang --blame-hang-timeout 30s` passed.
- `.\TestingSuite\TestManager.ps1 -TestId codex-sharded-backup-restore2 -TestName BackupRestoreSharded -GlobalTimeoutSeconds 300 -TestTimeoutSeconds 180` passed.
- Expanded `BackupRestoreSharded` coverage now includes preserve topology mismatch rejection, redistribute to a 3-shard target, single-shard append restore, and single-node backup restored into a sharded cluster.
- `.\TestingSuite\TestManager.ps1 -TestId codex-sharded-expanded2 -TestName BackupRestoreSharded -GlobalTimeoutSeconds 420 -TestTimeoutSeconds 300` passed.
- Failure-path `BackupRestoreSharded` coverage now includes an operational partial restore: shard 1 appends successfully, shard 2 fails because its target table is incompatible, and Chobo reports `PartiallySucceeded` with `shard-failed`, `table-partially-succeeded`, and restore-level `partially-succeeded` audit entries.
- `.\TestingSuite\TestManager.ps1 -TestId codex-sharded-failure-paths2 -TestName BackupRestoreSharded -GlobalTimeoutSeconds 480 -TestTimeoutSeconds 360` passed.
- Single-instance regression verification found and fixed a table-level restore status rollup issue: shard status reached `RESTORED`, but legacy `RestoreTableDto.clickHouseStatus` stayed `RESTORING`.
- `.\TestingSuite\TestManager.ps1 -TestId codex-core-regression2 -TestName BackupRestoreCoreScenarios -GlobalTimeoutSeconds 300 -TestTimeoutSeconds 180` passed.
- `.\TestingSuite\TestManager.ps1 -TestId codex-single-regression2 -TestName BackupRestoreSingleNode -GlobalTimeoutSeconds 240 -TestTimeoutSeconds 120` passed.
