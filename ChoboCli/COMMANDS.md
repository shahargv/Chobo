# ChoboCli Command Reference

All commands use:

```powershell
ChoboCli <subject> <verb> [options]
```

Authenticate once:

```powershell
ChoboCli server auth --server-url http://localhost:8080 --access-token <token>
```

Or pass auth per command:

```powershell
ChoboCli users list --server-url http://localhost:8080 --access-token <token>
```

## Users

```powershell
ChoboCli users list
ChoboCli users add --username operator
ChoboCli users remove --id <user-id>
ChoboCli users tokens --id <user-id>
ChoboCli users add-token --id <user-id> --name automation
ChoboCli users remove-token --id <user-id> --token-id <token-id>
```

`users add` prints the new access token once.
`users add-token` also prints the new access token once. Token list output contains metadata only, never raw token values.

## Clusters

```powershell
ChoboCli clusters list
ChoboCli clusters add --name prod --mode Cluster --node ch1:8123,ch2:8123 --username default --password secret --backup-restore-maxdop 3 --clickhouse-cluster-name prod_cluster
ChoboCli clusters add --name local --mode SingleInstance --host localhost --port 8123 --backup-restore-maxdop 1
ChoboCli clusters update --id <cluster-id> --name prod --mode Cluster --node ch1:8123,ch2:8123 --backup-restore-maxdop 3
ChoboCli clusters update-credentials --id <cluster-id> --username default --password secret
ChoboCli clusters test-connection --id <cluster-id>
ChoboCli clusters remove --id <cluster-id>
```

Credentials are write-only and are never printed by `clusters list`.

ChoboServer uses the official `ClickHouse.Driver` ADO.NET package over HTTP(S). Enter the ClickHouse HTTP or HTTPS port that ChoboServer can reach, such as `8123` for standard HTTP deployments.

For `Cluster` mode, `--clickhouse-cluster-name` should match the ClickHouse `system.clusters.cluster` value that describes the shards and replicas. Chobo uses that topology during backup/restore to choose one representative replica per shard. If the option is omitted, Chobo can auto-discover the name only when `system.clusters` contains exactly one cluster definition.
Cluster-level ClickHouse advanced settings are defaults for every backup or restore that uses the cluster:

```powershell
ChoboCli clusters update --id <cluster-id> --clickhouse-backup-setting max_backup_bandwidth=104857600 --clickhouse-restore-setting restore_threads=4
ChoboCli clusters update --id <cluster-id> --clickhouse-backup-settings-file .\backup-settings.json --clickhouse-restore-settings-json '{"restore_threads":4}'
```

## Targets

```powershell
ChoboCli targets list
ChoboCli targets add-s3 --name object-store --endpoint https://s3-compatible.example.com --bucket chobo-backups --access-key key --secret-key secret --force-path-style
ChoboCli targets update-s3 --id <target-id> --name object-store --endpoint https://s3-compatible.example.com --bucket chobo-backups
ChoboCli targets test-connection --id <target-id>
ChoboCli targets remove --id <target-id>
```

S3 credentials are write-only.

## Policies

```powershell
ChoboCli policies list
ChoboCli policies add --name all --source-cluster-id <cluster-id> --target-id <target-id>
ChoboCli policies add --name filtered --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json
ChoboCli policies add --name daily-schema --source-cluster-id <cluster-id> --schema-only
ChoboCli policies add --name protected --source-cluster-id <cluster-id> --target-id <target-id> --password-mode constant --backup-password <password>
ChoboCli policies add --name generated --source-cluster-id <cluster-id> --target-id <target-id> --password-mode generated-per-table-shard
ChoboCli policies add --name compressed --source-cluster-id <cluster-id> --target-id <target-id> --compression-method lzma --compression-level 3
ChoboCli policies update --id <policy-id> --name filtered --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json --full-retention-minutes 43200 --incremental-retention-minutes 10080 --min-backups-to-keep 7 --min-full-backups-to-keep 2 --max-age-hours-for-base-backup 168
ChoboCli policies evaluate --id <policy-id> --inventory-file .\inventory.json
ChoboCli policies remove --id <policy-id>
```

The source cluster is configured on the policy itself. Selector files only decide which database tables in that source cluster should be backed up. Use `--schema-only` for policies that store only captured DDL; schema-only policies do not take `--target-id` and do not support incremental backups.

Password protection and compression are optional and independent. Password modes are `none`, `constant`, and `generated-per-table-shard`. A constant password is write-only: omit `--backup-password` while updating an existing constant policy to preserve it. Supported compression methods are `store`, `deflate`, `bzip2`, `lzma`, `zstd`, and `xz`; `--compression-level` requires a method and cannot be used with `store`. Either feature creates `.zip` backup objects.

Use --max-age-hours-for-base-backup <hours> to override how old a full backup can be before incremental table-shard selection ignores it. Omit the option to inherit the live application default. Operators can change that default with ChoboCli settings set --key Chobo:BackupRestore:DefaultMaxAgeHoursForBaseBackup --value 168.
Policy-level settings override matching cluster defaults and apply to every scheduled or manual run from that policy. Policy restore settings are used as the current default when starting a restore from a backup created by the policy:

```powershell
ChoboCli policies add --name all --source-cluster-id <cluster-id> --target-id <target-id> --clickhouse-backup-setting backup_threads=4 --clickhouse-restore-setting restore_threads=4
ChoboCli policies update --id <policy-id> --clickhouse-backup-settings-json '{"max_backup_bandwidth":104857600}' --clickhouse-restore-settings-file .\restore-settings.json
```

Selector file with ordered include and exclude rules:

```json
{
  "version": 1,
  "rules": [
    {
      "action": "include",
      "database": { "kind": "all", "value": "*" },
      "table": { "kind": "all", "value": "*" }
    },
    {
      "action": "exclude",
      "database": { "kind": "exact", "value": "system" },
      "table": { "kind": "all", "value": "*" }
    },
    {
      "action": "exclude",
      "database": { "kind": "wildcard", "value": "tmp_*" },
      "table": { "kind": "all", "value": "*" }
    },
    {
      "action": "exclude",
      "database": { "kind": "wildcard", "value": "tenant_*" },
      "table": { "kind": "wildcard", "value": "*_scratch" }
    },
    {
      "action": "include",
      "database": { "kind": "exact", "value": "tenant_gold" },
      "table": { "kind": "exact", "value": "monthly_scratch" }
    },
    {
      "action": "exclude",
      "database": { "kind": "exact", "value": "sales" },
      "table": { "kind": "exact", "value": "fact_clickstream_raw" }
    }
  ]
}
```

Rules are evaluated from top to bottom. A matching `include` rule marks the table for backup, and a later matching `exclude` rule removes it again; a later `include` can add it back. Use `all` with `*` to match every database or table, `exact` for one name, and `wildcard` for simple patterns such as `tenant_*`, `fact_*`, or `*_scratch`.

The policy stores the selector JSON version separately from the JSON body so future selector formats can be evaluated by version.

Inventory file for `policies evaluate`:

```json
{
  "tables": [
    { "database": "sales", "table": "orders" },
    { "database": "sales", "table": "fact_clickstream_raw" },
    { "database": "tenant_gold", "table": "events" },
    { "database": "tenant_gold", "table": "monthly_scratch" },
    { "database": "tenant_demo", "table": "daily_scratch" },
    { "database": "system", "table": "query_log" }
  ]
}
```

## Schedules

```powershell
ChoboCli schedules list
ChoboCli schedules add --name nightly --policy-id <policy-id> --backup-type Full --cron "0 0 2 * * ?" --timezone UTC --missed-run-grace-period 00:05:00
ChoboCli schedules update --id <schedule-id> --name nightly --policy-id <policy-id> --backup-type Incremental --cron "0 0 */6 * * ?" --timezone UTC --missed-run-grace-period 00:10:00
ChoboCli schedules enable --id <schedule-id>
ChoboCli schedules disable --id <schedule-id>
ChoboCli schedules remove --id <schedule-id>
```

Cron expressions use Quartz-style fields. Omit `--missed-run-grace-period` to use the server default.

## Dashboard

```powershell
ChoboCli dashboard
ChoboCli dashboard --next-hours 12
ChoboCli dashboard show --next-hours 12
ChoboCli metrics show
```

The dashboard shows active backup runs, every schedule with last-run status and last successful completion time, and upcoming backup runs for the next window. Use `--next-hours <hours>` to choose that window. Active sharded backups include table and table-shard counts, including succeeded, failed, and running table-shard totals. The default future window is 6 hours.

The same server surface also exposes flat general metrics at `/api/v1/metrics`, available through `ChoboCli metrics show`. Metrics include seconds since the last successful backup completed for each policy, for example `Policies.TimeSecondsSinceLastPolicyBackup.nightly`, plus partial and failed backup counters per policy.

## Backups and restores

```powershell
ChoboCli backup manual --cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json
ChoboCli backup manual --policy-id <policy-id> --backup-type Incremental
ChoboCli backup manual --policy-id <schema-only-policy-id>
ChoboCli backups list --policy-id <policy-id>
ChoboCli backups list --cluster-name source --table-name orders
ChoboCli backups show --id <backup-id>
ChoboCli backups wait --id <backup-id> --timeout-seconds 300 --poll-seconds 2
ChoboCli backups recover --target-id <target-id> --scan-root backups
ChoboCli backups recover --target-id <target-id> --backup-path backups/policy-<policy-id>/_chobo/<backup-id>.json
ChoboCli schema backups
ChoboCli schema show --backup-id <backup-id> --database sales --table orders
ChoboCli schema export --backup-id <backup-id> --database sales

ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --target-table restored_orders
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --append --confirm-destructive
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout preserve
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout redistribute
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout single-node --source-shard 1
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout preserve --target-shard 2
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --table-mappings-json '[{"backupTableId":"<table-id>","targetDatabase":"restore","targetTable":"orders_restore","schemaOnly":false,"append":true,"allowSchemaMismatch":true}]' --confirm-destructive
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --table-mappings-file .\restore-tables.json
ChoboCli restores list
ChoboCli restores show --id <restore-id>
ChoboCli restores wait --id <restore-id> --timeout-seconds 300 --poll-seconds 2
```


Manual backup and restore commands inherit cluster and policy settings unless you pass any advanced settings option. When you do, the CLI first loads the inherited effective settings, applies your additions and removals, and sends the final dictionary for that run:

```powershell
ChoboCli backup manual --policy-id <policy-id> --clickhouse-backup-setting max_backup_bandwidth=52428800 --remove-clickhouse-backup-setting backup_threads
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --clickhouse-restore-settings-json '{"restore_threads":2}' --remove-clickhouse-restore-setting max_backup_bandwidth
```

Advanced settings options are `--clickhouse-backup-setting name=value`, `--clickhouse-backup-settings-json`, `--clickhouse-backup-settings-file`, `--remove-clickhouse-backup-setting`, and the matching `restore` variants. Values must be strings, numbers, or booleans. Chobo rejects settings it manages internally, including `base_backup` and `allow_non_empty_tables`. ClickHouse documents supported backup/restore setting names at <https://clickhouse.com/docs/operations/backup/overview#settings>.
Backup and restore commands return run records immediately. `wait` is a client-side polling helper. Run JSON includes `startedAt` and `endedAt`; `endedAt` is when the actual run process reached a terminal outcome, including success, partial success, failure, or cancellation, not the last metadata update time.

Per-table restore mappings may include `createTableSqlOverride` when a DBA needs Chobo to create a target table from corrected DDL instead of the captured backup schema. The override is used only for that restore request; Chobo still rewrites the target database/table name from the mapping.

`backups recover` rebuilds SQLite backup metadata from storage-side manifests after local database loss. Use `--scan-root` to scan a bucket/root for manifests, or `--backup-path` for one known manifest path. The supplied target provides S3 credentials; recovered ClickHouse clusters still need `clusters update-credentials` because manifests do not store ClickHouse credentials. If a manifest declares storage data paths that are already missing, recovery imports the backup as `PartiallySucceeded` so remaining data can still be managed.

Schema+data backups submit ClickHouse `BACKUP TABLE` only for `*MergeTree` engines. Other selected engines are captured as schema metadata only. Schema-only backups do not write storage objects or manifests.

For MergeTree-family tables in `Cluster` mode, one logical backup table contains one shard task for each source shard. Chobo does not run ClickHouse `BACKUP ... ON CLUSTER`; it queries topology, picks one representative replica per shard, runs the shard operations manually, and records the selected source node in the backup metadata. `backups show` and `backups wait` include per-table `shards` arrays with source shard number, selected node, storage path, status, operation id, and error details.

Restore layout controls:

- `preserve`: default. Source shard N restores to target shard N. It is valid for a different target cluster only when every selected source shard number exists on the target cluster; otherwise choose `redistribute`.
- `redistribute`: maps selected source shards across the target topology in target shard order. This is the explicit choice for restoring from one shard count to another.
- `single-node`: restores selected source shards through the first target node, useful for sharded-to-standalone restore or single-shard extraction.
- `--source-shard <n>` limits restore to one backed-up source shard.
- `--target-shard <n>` forces selected source shards to one target shard.

Run and table statuses can be `PartiallySucceeded` when at least one required shard succeeds and at least one required shard fails. In that case, inspect `restores show --id <restore-id>` for shard-level status and error details, then inspect `audit show` for `shard-failed`, `table-partially-succeeded`, and restore-level `partially-succeeded` entries.

## Garbage Collector

```powershell
ChoboCli gc status
ChoboCli gc queue
ChoboCli gc run
ChoboCli gc run-one --id <backup-id>
```

`gc queue` lists backup entities waiting for cleanup, including failed backups eligible by policy and canceled/delete-requested backups with remaining storage metadata. `gc run-one` processes only the requested queued backup id and is safe if the scheduled garbage collector already cleaned it.
## Logs

```powershell
ChoboCli logs show --last 500
ChoboCli logs show --start-time 2026-05-09T10:00:00Z --end-time 2026-05-09T11:30:00Z
ChoboCli logs clear --before 2026-05-08T00:00:00Z
```

## Audit

```powershell
ChoboCli audit show --last 500
ChoboCli audit show --start-time 2026-05-09T10:00:00Z --end-time 2026-05-09T11:30:00Z
ChoboCli audit clear --before 2026-05-08T00:00:00Z
```

## Import And Export

```powershell
ChoboCli data export --output .\chobo-data.json
ChoboCli data import --file .\chobo-data.json

ChoboCli config export --output .\chobo-config.json
ChoboCli config import --file .\chobo-config.json
```

`data` exports all restorable Chobo metadata except audit entries and application logs. `config` is configuration-only and does not preserve backup/restore history.
