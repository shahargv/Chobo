# Setting Up Backups

Backups are built from three configured resources:

- A ClickHouse cluster.
- An S3-compatible backup target.
- A backup policy that joins the source cluster, target, table selector, and optional retention rules.

Schedules then run a policy on a Quartz-style cron expression. Manual backups can run without a saved policy.

## Add A ClickHouse Source

Single-node ClickHouse:

```powershell
ChoboCli clusters add --name prod-single --mode SingleInstance --host clickhouse-1.example.com --port 9000 --username default --password <password>
```

Clustered ClickHouse:

```powershell
ChoboCli clusters add --name prod-cluster --mode Cluster --node ch1:9000,ch2:9000,ch3:9000 --username default --password <password> --backup-restore-maxdop 3 --clickhouse-cluster-name prod_cluster
```

Notes:

- Chobo talks to ClickHouse through the official `ClickHouse.Driver` ADO.NET package, which uses the HTTP(S) interface.
- Existing native-default port values remain accepted: Chobo maps `9000` to HTTP port `8123`, and maps TLS port `9440` to HTTPS port `8443`.
- Use `--tls` when the ClickHouse HTTPS endpoint requires TLS.
- `--backup-restore-maxdop` limits parallel table work for this cluster and overrides the server default.
- For `Cluster` mode, Chobo reads `system.clusters`, selects one representative replica per shard, and performs shard-level backup work manually.

## Add An S3 Target

```powershell
ChoboCli targets add-s3 --name prod-s3 --endpoint https://s3.example.com --region us-east-1 --bucket chobo-backups --path-prefix prod --access-key <key> --secret-key <secret>
```

For MinIO or other path-style providers:

```powershell
ChoboCli targets add-s3 --name minio --endpoint http://minio:9000 --bucket data-bucket --access-key <key> --secret-key <secret> --force-path-style
```

The endpoint must be reachable from the ClickHouse nodes, not only from ChoboServer. ClickHouse is the process that writes backup objects during `BACKUP`.

## Create A Selector

A selector decides which tables are included. Rules are evaluated top to bottom. A later rule can exclude a table that was included earlier, or include it again.

Example `policy-selector.json`:

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
    }
  ]
}
```

Supported match kinds:

- `all` with value `*`.
- `exact` for one database or table name.
- `wildcard` for simple patterns such as `tenant_*`, `fact_*`, or `*_scratch`.

Chobo automatically excludes ClickHouse `system`, `information_schema`, and `INFORMATION_SCHEMA` from backup inventory.

## Create A Policy

Without lifecycle retention:

```powershell
ChoboCli policies add --name nightly-prod --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json
```

With lifecycle retention:

```powershell
ChoboCli policies add --name nightly-prod --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json --full-retention-minutes 43200 --incremental-retention-minutes 10080 --min-backups-to-keep 7 --min-full-backups-to-keep 2
```

With failed-backup garbage collection:

```powershell
ChoboCli policies add --name nightly-prod --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json --full-retention-minutes 43200 --incremental-retention-minutes 10080 --min-backups-to-keep 7 --min-full-backups-to-keep 2 --failed-backup-retention-mode DeleteByGarbageCollectorAfterFailure
```

Schema-only policies do not require backup storage and always run as full schema captures:

```powershell
ChoboCli policies add --name daily-schema --source-cluster-id <cluster-id> --schema-only
```

When a cluster is created, Chobo also creates a reserved daily UTC schema-only policy and schedule for that cluster. This default keeps schema history available from the start, is visible as a system default, and is ignored by the dashboard onboarding checklist.

Evaluate a selector against a known inventory:

```powershell
ChoboCli policies evaluate --id <policy-id> --inventory-file .\inventory.json
```

## Create A Schedule

Schedules use Quartz-style cron expressions with at least six fields.

Nightly at 02:00 UTC:

```powershell
ChoboCli schedules add --name nightly-prod --policy-id <policy-id> --backup-type Full --cron "0 0 2 * * ?" --timezone UTC --missed-run-grace-period 00:05:00
```

Every six hours:

```powershell
ChoboCli schedules add --name six-hour-prod --policy-id <policy-id> --backup-type Full --cron "0 0 */6 * * ?" --timezone UTC
```

Use `--backup-type Full` for periodic base backups and `--backup-type Incremental` for cumulative incrementals based on the latest successful full backup for the policy. Schema-only policies do not support incremental schedules; they always run as full schema captures.

Manage schedules:

```powershell
ChoboCli schedules list
ChoboCli schedules disable --id <schedule-id>
ChoboCli schedules enable --id <schedule-id>
ChoboCli schedules update --id <schedule-id> --name nightly-prod --policy-id <policy-id> --backup-type Full --cron "0 30 2 * * ?" --timezone UTC
ChoboCli schedules remove --id <schedule-id>
```

## Run A Manual Backup

```powershell
ChoboCli backup manual --cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json
ChoboCli backup manual --policy-id <policy-id> --backup-type Incremental
ChoboCli backup manual --policy-id <schema-only-policy-id>
```

The command returns a run record immediately. Wait for completion:

```powershell
ChoboCli backups wait --id <backup-id> --timeout-seconds 300 --poll-seconds 2
```

Inspect details:

```powershell
ChoboCli backups show --id <backup-id>
ChoboCli backups list --policy-id <policy-id>
ChoboCli backups list --cluster-name prod-cluster --table-name sales.orders
ChoboCli backups list --status PartiallySucceeded
```

## What Chobo Backs Up

For each selected table, Chobo preserves the table definition so the backup can be inspected and used for restore planning later.

Schema+data backups submit ClickHouse `BACKUP TABLE` only for `*MergeTree` engines, including replicated MergeTree variants. Selected tables with other engines, such as `Log` and `Join`, keep schema metadata only and are marked with `dataBackedUp = false`.

Schema-only backups never submit table data backup work, never write storage manifests, and do not require or use an S3 target. In cluster mode, schema-only capture queries every source node and dedupes objects by `database.table`, choosing the first schema from deterministic shard, replica, host, and port ordering.

For clustered sources, Chobo:

- Reads topology from `system.clusters`.
- Picks one representative replica per shard, preferring lower `errors_count`, lower replica number, then stable host and port ordering.
- Runs a separate backup operation for each selected shard.
- Records per-shard S3 path, selected node, ClickHouse operation id, ClickHouse status, and error details.

Chobo does not use ClickHouse `BACKUP ... ON CLUSTER`.

## Browse Captured Schema

Schema browsing reads Chobo metadata only; it does not connect to ClickHouse or S3.

```powershell
ChoboCli schema backups
ChoboCli schema show --backup-id <backup-id>
ChoboCli schema show --backup-id <backup-id> --database sales --table orders
ChoboCli schema export --backup-id <backup-id>
ChoboCli schema export --backup-id <backup-id> --database sales
```

The GUI has a Schema Browser tab with a backup selector, database/table tree, SQL viewer, and export buttons for all schema SQL or a single database.
## Backup Paths

Backup object paths use this logical structure:

```text
backups/full/<policy-or-manual>/<database>/<table>/<timestamp>/<backup-id>
backups/incremental/<policy>/<database>/<table>/parent-full-<full-backup-id>/<timestamp>/<backup-id>
```

Sharded tables add:

```text
shards/shard-0001
shards/shard-0002
```

If the S3 target has `--path-prefix`, Chobo prepends it to the ClickHouse S3 URL and to server-side deletion requests.

## Storage Metadata Manifests

Each schema+data backup writes a Chobo metadata manifest into storage so backup metadata can be recovered even if local SQLite state is lost. Schema-only backups do not create storage objects or manifests. The manifest is stored as:

```text
<backup table or shard path>/_chobo/backup-metadata.v1.json
```

Chobo writes the full backup-run manifest under each table path and each shard path. This intentionally duplicates the same metadata across cleanup-relevant prefixes, so normal backup deletion removes the manifest with the backup objects.

The manifest includes the backup run, table and shard records, schema definitions, source cluster topology, policy, schedule, and S3 target settings. It never includes ClickHouse username/password or S3 access/secret keys. Failed backups are written too when enough metadata exists, so storage recovery can rebuild failure history for diagnostics and lifecycle handling.

When changing backup metadata, update the storage manifest contract and writer in the same change. A backup is not independently recoverable unless the manifest contains the information needed to recreate the SQLite backup, table, shard, schema, policy, target, and source-cluster rows.

## Monitoring Backups

Dashboard:

```powershell
ChoboCli dashboard --next-hours 12
```

Metrics:

```powershell
ChoboCli metrics show
```

Logs and audit:

```powershell
ChoboCli logs show --last 500
ChoboCli audit show --last 200
```

Important backup statuses:

- `Queued`
- `Running`
- `Succeeded`
- `PartiallySucceeded`
- `Failed`
- `ManualDeleteRequested`
- `ManualDeleted`
- `FailedBackupDeleteRequested`
- `FailedBackupDeletedByGarbageCollector`
- `BackupExpiredDeleteStarted`
- `BackupExpiredDeleted`

When a backup is `PartiallySucceeded`, inspect the table and shard arrays in `backups show`.



