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
ChoboCli clusters add --name prod --mode Cluster --node ch1:9000,ch2:9000 --username default --password secret --backup-restore-maxdop 3 --clickhouse-cluster-name prod_cluster
ChoboCli clusters add --name local --mode SingleInstance --host localhost --port 9000
ChoboCli clusters update --id <cluster-id> --name prod --mode Cluster --node ch1:9000,ch2:9000
ChoboCli clusters remove --id <cluster-id>
```

Credentials are write-only and are never printed by `clusters list`.

ChoboServer uses the official `ClickHouse.Driver` ADO.NET package over HTTP(S). For compatibility, configured native-default ports are accepted: `9000` maps to HTTP `8123`, and TLS `9440` maps to HTTPS `8443`. Pass a custom HTTP(S) port directly when your ClickHouse deployment uses one.

For `Cluster` mode, `--clickhouse-cluster-name` should match the ClickHouse `system.clusters.cluster` value that describes the shards and replicas. Chobo uses that topology during backup/restore to choose one representative replica per shard. If the option is omitted, Chobo can auto-discover the name only when `system.clusters` contains exactly one cluster definition.

## Targets

```powershell
ChoboCli targets list
ChoboCli targets add-s3 --name minio --endpoint http://localhost:9000 --bucket data-bucket --access-key key --secret-key secret --force-path-style
ChoboCli targets update-s3 --id <target-id> --name minio --endpoint http://localhost:9000 --bucket data-bucket
ChoboCli targets remove --id <target-id>
```

S3 credentials are write-only.

## Policies

```powershell
ChoboCli policies list
ChoboCli policies add --name all --source-cluster-id <cluster-id> --target-id <target-id>
ChoboCli policies add --name filtered --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json
ChoboCli policies update --id <policy-id> --name filtered --source-cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json
ChoboCli policies evaluate --id <policy-id> --inventory-file .\inventory.json
ChoboCli policies remove --id <policy-id>
```

The source cluster is configured on the policy itself. Selector files only decide which database tables in that source cluster should be backed up.

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

The dashboard shows active backup runs, every schedule with last-run status and last successful completion time, and projected schedule runs for the next window. Active sharded backups include table and shard counts, including succeeded, failed, and running shard totals. The default future window is 6 hours.

The same server surface also exposes flat general metrics at `/api/v1/metrics`, available through `ChoboCli metrics show`. Metrics include seconds since the last successful backup completed for each policy, for example `Policies.TimeSecondsSinceLastPolicyBackup.nightly`, plus partial and failed backup counters per policy.

## Backups And Restores

```powershell
ChoboCli backup manual --cluster-id <cluster-id> --target-id <target-id> --selector-file .\policy-selector.json
ChoboCli backups list --policy-id <policy-id>
ChoboCli backups list --cluster-name source --table-name orders
ChoboCli backups show --id <backup-id>
ChoboCli backups wait --id <backup-id> --timeout-seconds 300 --poll-seconds 2

ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --target-table restored_orders
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --append
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout preserve
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout redistribute
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout single-node --source-shard 1
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout preserve --target-shard 2
ChoboCli restores list
ChoboCli restores show --id <restore-id>
ChoboCli restores wait --id <restore-id> --timeout-seconds 300 --poll-seconds 2
```

Backup and restore commands return run records immediately. `wait` is a client-side polling helper.

For MergeTree-family tables in `Cluster` mode, one logical backup table contains one shard task for each source shard. Chobo does not run ClickHouse `BACKUP ... ON CLUSTER`; it queries topology, picks one representative replica per shard, runs the shard operations manually, and records the selected source node in the backup metadata. `backups show` and `backups wait` include per-table `shards` arrays with source shard number, selected node, S3 path, status, operation id, and error details.

Restore layout controls:

- `preserve`: default. Source shard N restores to target shard N when source and target shard counts match. If counts differ, the request fails with a clear message unless another layout is chosen.
- `redistribute`: maps selected source shards across the target topology in target shard order. This is the explicit choice for restoring from one shard count to another.
- `single-node`: restores selected source shards through the first target node, useful for sharded-to-standalone restore or single-shard extraction.
- `--source-shard <n>` limits restore to one backed-up source shard.
- `--target-shard <n>` forces selected source shards to one target shard.

Run and table statuses can be `PartiallySucceeded` when at least one required shard succeeds and at least one required shard fails. In that case, inspect `restores show --id <restore-id>` for shard-level status and error details, then inspect `audit show` for `shard-failed`, `table-partially-succeeded`, and restore-level `partially-succeeded` entries.

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

`data` includes logs and audits. `config` excludes logs and audits.
