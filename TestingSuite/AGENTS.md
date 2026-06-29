# TestingSuite Agent Notes

`TestingSuite` is Chobo's Docker Compose based system-test harness. Tests declare the ClickHouse, ChoboServer, and S3 resources they need, then `TestManager.ps1` generates an isolated Compose environment, runs the selected tests inside the `test-runner` container, collects logs/artifacts, and tears the environment down.

Use `TestingSuite/README.md` as the source of truth for commands, resource syntax, generated artifacts, and debugging. Prefer declarative tests under `TestingSuite/Tests/<TestName>/TestDefinition.psd1`; use custom PowerShell only when declarative setup/action/verify steps cannot express the scenario.

When running tests, always pass a stable `-TestId` plus both `-GlobalTimeoutSeconds` and `-TestTimeoutSeconds`. Use `-CleanTestResults` only for a final verification run after debugging artifacts are no longer needed.

## Included Tests

- `BackupMetadataRecovery`: rebuilds backup metadata from S3 manifests after SQLite loss, including single-node, sharded, incremental, and failed backup records.
- `BackupRestoreCancellation`: cancels running backup and restore operations and verifies ClickHouse kill, cleanup, and audit behavior.
- `BackupRestoreCoreScenarios`: covers core single-node backup and restore flows such as rename, full database restore, append, schema mismatch, and schema-only engines.
- `BackupRestoreReplicatedNodeDown`: verifies backup and restore on a functioning two-shard, two-replica ClickHouse cluster with one replica down.
- `BackupRestoreSharded`: exercises sharded backup and restore with one selected node per source shard.
- `BackupRestoreSingleNode`: backs up a MergeTree table from one single-node ClickHouse instance and restores it to another.
- `BackupRetentionCleanup`: verifies retention, pinning, manual deletion, failed cleanup, restart resume, audit, and physical storage cleanup.
- `BackupRetentionCleanupSharded`: verifies asynchronous cleanup removes every storage object for a sharded backup.
- `BootstrapCredentialPersistence`: verifies bootstrap, keyed credential persistence, and connection tests across server restart.
- `BootstrapFirstSetup`: verifies production first setup, GUI onboarding, install finalization, and anonymous token closure.
- `ChoboCrudSmoke`: exercises ChoboCli authentication and CRUD APIs through ChoboServer.
- `FailingBasicTest`: intentionally fails to validate failure reporting and artifact capture.
- `FailureScenario`: exercises named backup and restore failure modes and verifies diagnostic run records, audit, logs, and dashboard output.
- `ImportExportRoundTrip`: verifies data export/import round-trips operational metadata while excluding audit and logs.
- `IncrementalBackupSharded`: verifies cumulative incremental backup and restore on a sharded cluster, including per-shard parent paths and new-table full fallback.
- `IncrementalBackupSingleNode`: verifies cumulative incremental backup and restore on one ClickHouse node, including schema changes and new-table full fallback.
- `IncrementalMultipleFullParents`: verifies one incremental backup can depend on multiple full backups and that retention/deletion preserve or cascade correctly.
- `LargeOnTimeBackupGc`: debug-only cleanup test using a 2000-2010 slice of the public ClickHouse OnTime dataset.
- `RestoreRedistributeTargetShardSubset`: restores from three source shards into a selected two-shard target pool while keeping the table declared on every target shard.
- `SchemaOnlyLargeSingleNode`: runs a schema-only backup for 1,000 tables and verifies summary behavior.
- `SchemaVersionRejection`: mutates SQLite schema version above the supported server version and verifies startup rejection.
- `SmokeCreateTables`: creates tables on one standalone node and two clusters, inserts deterministic data, and validates each resource.
- `SqliteSelfBackup`: verifies local SQLite self-backups plus related logs and audit entries.