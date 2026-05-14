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
ChoboCli policies add --name nightly-prod --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json --retention-minutes 10080 --min-backups-to-keep 7
```

With failed-backup garbage collection:

```powershell
ChoboCli policies add --name nightly-prod --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json --retention-minutes 10080 --min-backups-to-keep 7 --failed-backup-retention-mode DeleteByGarbageCollectorAfterFailure
```

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

Only full backups are currently supported by execution. Keep `--backup-type Full`.

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

For each selected table, Chobo preserves the table definition so the backup can be inspected and used for restore planning later. Tables that contain ClickHouse-managed data also have their data captured as part of the backup run.

For tables that do not have backupable table data, Chobo records the table definition and marks the backup table as schema-only.

For clustered sources, Chobo:

- Reads topology from `system.clusters`.
- Picks one representative replica per shard, preferring lower `errors_count`, lower replica number, then stable host and port ordering.
- Runs a separate backup operation for each selected shard.
- Records per-shard S3 path, selected node, ClickHouse operation id, ClickHouse status, and error details.

Chobo does not use ClickHouse `BACKUP ... ON CLUSTER`.

## Backup Paths

Backup object paths use this logical structure:

```text
backups/<database>/<table>/<policy-or-manual>/<backup-type>/<timestamp>/<backup-id>
```

Sharded tables add:

```text
shards/shard-0001
shards/shard-0002
```

If the S3 target has `--path-prefix`, Chobo prepends it to the ClickHouse S3 URL and to server-side deletion requests.

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

