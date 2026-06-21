# Setting Up Backups

Backups are built from three configured resources:

- A ClickHouse cluster.
- An S3-compatible backup target.
- A backup policy that joins the source cluster, target, table selector, and optional retention rules.

Schedules then run a policy on a Quartz-style cron expression. Manual backups can run without a saved policy.

## Add A ClickHouse Source

Single-node ClickHouse:

```powershell
ChoboCli clusters add --name prod-single --mode SingleInstance --host clickhouse-1.example.com --port 8123 --username default --password <password>
```

Clustered ClickHouse:

```powershell
ChoboCli clusters add --name prod-cluster --mode Cluster --node ch1:8123,ch2:8123,ch3:8123 --username default --password <password> --backup-restore-maxdop 3 --clickhouse-cluster-name prod_cluster
```

Notes:

- Chobo talks to ClickHouse through the official `ClickHouse.Driver` ADO.NET package, which uses the HTTP(S) interface.
- Use `--tls` when the ClickHouse HTTPS endpoint requires TLS.
- `--backup-restore-maxdop` limits parallel table work for this cluster and overrides the server default.
- For `Cluster` mode, Chobo reads `system.clusters`, selects one representative replica per shard, and performs shard-level backup work manually.

## Add An S3 Target

```powershell
ChoboCli targets add-s3 --name prod-s3 --endpoint https://s3.example.com --region us-east-1 --bucket chobo-backups --path-prefix prod --access-key <key> --secret-key <secret>
```

For S3-compatible providers that require path-style addressing:

```powershell
ChoboCli targets add-s3 --name object-store --endpoint https://s3-compatible.example.com --bucket chobo-backups --access-key <key> --secret-key <secret> --force-path-style
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

Schema-only backups never submit table data backup work, never write storage manifests, and do not require or use an S3 target. In cluster mode, schema-only capture queries every source node and deduplicates objects by `database.table`, choosing the first schema from deterministic shard, replica, host, and port ordering.

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




## Sample Backup Outputs

A manual backup returns a run record immediately:

```powershell
ChoboCli backup manual --policy-id 4e97f04c-b1ed-4766-9a3b-5162d02f0475 --backup-type Full
```

```json
{
  "id": "6b63350a-7073-49a3-884e-f77ee7f58433",
  "policyName": "sales-nightly",
  "status": "Queued",
  "backupType": "Full",
  "contentMode": "SchemaAndData",
  "tableCount": 12,
  "createdAt": "2026-06-21T02:00:00Z"
}
```

Use `wait` for runbooks and automation:

```powershell
ChoboCli backups wait --id 6b63350a-7073-49a3-884e-f77ee7f58433 --timeout-seconds 1800 --poll-seconds 5
```

Successful sharded backup output includes table and shard details:

```json
{
  "id": "6b63350a-7073-49a3-884e-f77ee7f58433",
  "status": "Succeeded",
  "startedAt": "2026-06-21T02:00:04Z",
  "endedAt": "2026-06-21T02:04:18Z",
  "tables": [
    {
      "database": "sales",
      "table": "orders",
      "status": "Succeeded",
      "dataBackedUp": true,
      "shards": [
        {
          "sourceShardNumber": 1,
          "host": "ch-s1-r1.example.com",
          "port": 8123,
          "useTls": false,
          "status": "Succeeded",
          "s3Path": "backups/full/policy-sales/sales/orders/20260621T020004Z/6b63350a/shards/shard-0001"
        },
        {
          "sourceShardNumber": 2,
          "host": "ch-s2-r1.example.com",
          "port": 8123,
          "useTls": false,
          "status": "Succeeded",
          "s3Path": "backups/full/policy-sales/sales/orders/20260621T020004Z/6b63350a/shards/shard-0002"
        }
      ]
    }
  ]
}
```

Progress output is compact and useful during long backups:

```powershell
ChoboCli backups progress --id 6b63350a-7073-49a3-884e-f77ee7f58433
```

```text
Backup 6b63350a-7073-49a3-884e-f77ee7f58433 Running tables=12
  sales.line_items  Running  shards=3 queued=0 running=1 completed=2 succeeded=2 failed=0 skipped=0
  sales.orders      Succeeded shards=3 queued=0 running=0 completed=3 succeeded=3 failed=0 skipped=0
```

In the web GUI, open **Backups** and then a backup detail page to see the same table, shard, log, and audit context.

![Backup details with table, shard, log, and audit context](assets/readme/backup-detail.png)

## Monitoring And Alerting

Use the dashboard for daily human review and metrics for automated alerting.

Daily DBA checklist:

```powershell
ChoboCli dashboard --next-hours 24
ChoboCli metrics show
ChoboCli backups list --status Failed
ChoboCli backups list --status PartiallySucceeded
```

The Prometheus scrape endpoint is:

```text
/api/v1/metrics/prometheus
```

Useful alert conditions:

- no successful backup for a production policy within the expected recovery point objective;
- any failed or partially succeeded backup on a production policy;
- scheduled backups not appearing in the dashboard projection;
- repeated cleanup failures in retention or garbage collection;
- backup queue pressure that delays scheduled work.

Example Prometheus-style checks should be adapted to your metric names and labels:

```yaml
- alert: ChoboPolicyBackupTooOld
  expr: Chobo_Policies_TimeSecondsSinceLastPolicyBackup_sales_nightly > 90000
  for: 10m
  labels:
    severity: warning
  annotations:
    summary: sales-nightly has not completed a backup within 25 hours

- alert: ChoboBackupFailures
  expr: increase(Chobo_Policies_FailedBackupCount_sales_nightly[1h]) > 0
  for: 5m
  labels:
    severity: critical
```

`ChoboCli metrics show` exposes the same operational signals as JSON. Look especially for time since last successful policy backup and failed or partially failed backup counters.

## Cancellation

You can cancel a queued or running backup:

```powershell
ChoboCli backups cancel --id <backup-id>
```

Cancellation is best used when the request is clearly wrong, such as the wrong selector or target. If ClickHouse has already started async backup work, inspect the backup details, logs, and S3 prefixes afterward. A canceled or partially completed run may still have storage objects that need normal cleanup or investigation.
## Incremental Backups

Incremental backups are tied to the latest successful full backup for the policy. Use them when your ClickHouse backup strategy and retention policy are designed around full-plus-incremental chains.

Operational rules:

- run and verify a full backup before scheduling incrementals;
- keep enough full backups so incrementals have a usable parent;
- do not delete or force-delete parent full backups unless you understand which incrementals depend on them;
- pin important full backups before a risky maintenance window;
- prefer full backups for simple environments where storage and backup duration are acceptable.

Restores still start from a backup run. Chobo records parent relationships so lifecycle cleanup avoids deleting full backups that are still needed by retained incrementals.