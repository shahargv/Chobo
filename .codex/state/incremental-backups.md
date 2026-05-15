# Incremental Backups Tracking

## Goal
Support cumulative, table-based incremental backups with ClickHouse `base_backup`, including mixed full/incremental table and shard planning, lineage visibility, retention-safe deletion, failed-backup garbage collection, and dedicated single-node/sharded coverage.

## Implementation State
- [x] Contracts expose backup type and table/shard effective type plus parent full table/shard ids.
- [x] Manual incremental backups require a policy.
- [x] Scheduled incremental backups are enqueued instead of skipped.
- [x] Backup runner plans incremental parent tables and per-shard fallback.
- [x] S3 paths distinguish `full` from `incremental` and include parent full backup id for incremental paths.
- [x] ClickHouse backup execution passes `base_backup` for incremental table/shard work.
- [x] Retention protects full parents while dependent incrementals are live.
- [x] Manual delete cascades from full parent to dependent incrementals and respects pinned descendants unless forced.
- [x] Failed-backup garbage collector handles failed incrementals, failed full parents, and orphaned incrementals.
- [x] Dedicated system tests for single-node incremental restore are fully validated.
- [x] Dedicated system tests for sharded incremental restore are fully validated.
- [x] Documentation and CLI command reference are refreshed.

## Test State
- [x] Unit coverage for mixed full/incremental planning.
- [x] Unit coverage for per-shard parent selection and fallback.
- [x] Unit coverage for retention parent/child delete policy.
- [x] Unit coverage for failed-backup GC dependency handling.
- [x] Unit coverage for orphaned incremental GC.
- [x] `IncrementalBackupSingleNode` TestingSuite case.
- [x] `IncrementalBackupSharded` TestingSuite case.
