# Onboarding And Initial Configuration

This guide covers the first setup steps after Chobo is installed.

## Core Terms

**Cluster** means the ClickHouse source or restore target that Chobo can connect to. A cluster can be a single ClickHouse instance or a ClickHouse cluster with shards and replicas.

**Access node** means a ClickHouse host and port Chobo can connect to. In cluster mode, Chobo uses access nodes to discover topology from `system.clusters`.

**Backup storage** or **target** means S3-compatible storage where ClickHouse writes backup objects.

**Policy** means the saved backup definition: source cluster, storage target, table selector, content mode, and retention settings.

**Schedule** means the recurring time rule that runs a policy.

**Backup run** means one execution of a backup policy or one manual backup request.

**Restore run** means one restore operation created from an existing backup.

## First Login

Open the Chobo web GUI at `http://<server-host>:8080`.

![Initial Chobo install screen](assets/production-setup/00-install.png)

On a fresh data directory, Chobo asks you to create the first admin user and access token. Save the token in a password manager. Chobo shows raw tokens only once.

After login, the dashboard shows setup progress, active work, recent backup status, and upcoming scheduled work.

![Chobo dashboard after sign-in](assets/production-setup/01-dashboard.png)

CLI equivalent:

```powershell
ChoboCli server auth --server-url http://chobo.example.com:8080 --access-token <token>
ChoboCli dashboard --next-hours 1
```

To check the server version directly:

```bash
curl -H "Authorization: Bearer <token>" http://chobo.example.com:8080/api/v1/server/version
```

Example output:

```json
{
  "productName": "Chobo",
  "productVersion": "0.1.0",
  "apiVersion": 1,
  "schemaVersion": 1,
  "databaseSchemaVersion": 1
}
```

## Add A ClickHouse Source

In the web GUI, open **Clusters**, choose **Add cluster**, and enter the ClickHouse connection details.

![Add ClickHouse cluster in the web GUI](assets/production-setup/02-add-cluster.png)

For one ClickHouse instance:

```powershell
ChoboCli clusters add `
  --name prod-single `
  --mode SingleInstance `
  --host clickhouse-1.example.com `
  --port 8123 `
  --username backup_operator `
  --password <password>
```

For a sharded ClickHouse cluster:

```powershell
ChoboCli clusters add `
  --name prod-cluster `
  --mode Cluster `
  --node ch-s1-r1.example.com:8123,ch-s2-r1.example.com:8123,ch-s3-r1.example.com:8123 `
  --username backup_operator `
  --password <password> `
  --backup-restore-maxdop 3 `
  --clickhouse-cluster-name prod_cluster
```

Then test the connection:

```powershell
ChoboCli clusters test-connection --id <cluster-id>
```

Example output:

```json
{
  "succeeded": true,
  "message": "Connected to ClickHouse and discovered 3 shard(s)."
}
```

Chobo connects to ClickHouse through the HTTP(S) interface. Enter the ClickHouse HTTP or HTTPS port that ChoboServer can reach, such as `8123` for standard HTTP deployments.

## Add S3-Compatible Storage

In the web GUI, open **Backup Storage**, choose **Add storage**, and enter the S3 endpoint, bucket, optional path prefix, and credentials.

![Add S3-compatible backup storage in the web GUI](assets/production-setup/03-add-storage.png)

CLI example:

```powershell
ChoboCli targets add-s3 `
  --name prod-s3 `
  --endpoint https://s3.example.com `
  --region us-east-1 `
  --bucket chobo-backups `
  --path-prefix prod `
  --access-key <access-key> `
  --secret-key <secret-key>
```

For an S3-compatible provider that requires path-style addressing:

```powershell
ChoboCli targets add-s3 `
  --name object-store-backups `
  --endpoint https://s3-compatible.example.com `
  --bucket clickhouse-backups `
  --access-key <access-key> `
  --secret-key <secret-key> `
  --force-path-style
```

Important: the S3 endpoint must be reachable from ClickHouse nodes, not only from ChoboServer. ClickHouse performs the actual `BACKUP` and `RESTORE` object I/O.

## Create The First Policy

Open **Policies** and create a policy that selects the tables you want to protect.

![Create a backup policy in the web GUI](assets/production-setup/04-add-policy.png)

A good first policy includes application databases, excludes temporary data, runs manually once, and is inspected before scheduling.

CLI selector file:

```json
{
  "version": 1,
  "rules": [
    {
      "action": "include",
      "database": { "kind": "wildcard", "value": "sales*" },
      "table": { "kind": "all", "value": "*" }
    },
    {
      "action": "exclude",
      "database": { "kind": "wildcard", "value": "*_tmp" },
      "table": { "kind": "all", "value": "*" }
    }
  ]
}
```

Create the policy:

```powershell
ChoboCli policies add `
  --name sales-nightly `
  --source-cluster-id <cluster-id> `
  --target-id <target-id> `
  --selector-file .\sales-selector.json `
  --full-retention-minutes 43200 `
  --min-backups-to-keep 7
```

## Create The First Schedule

Open **Schedules** and add a recurring schedule for the policy.

![Create a backup schedule in the web GUI](assets/production-setup/05-add-schedule.png)

CLI example, daily at 02:00 UTC:

```powershell
ChoboCli schedules add `
  --name sales-nightly-0200 `
  --policy-id <policy-id> `
  --backup-type Full `
  --cron "0 0 2 * * ?" `
  --timezone UTC `
  --missed-run-grace-period 00:05:00
```

Check upcoming work:

```powershell
ChoboCli dashboard --next-hours 12
```

## Prove The Setup Works

Before relying on schedules, run one small end-to-end validation.

1. Test ClickHouse and confirm the discovered topology.

```powershell
ChoboCli clusters test-connection --id <cluster-id>
ChoboCli clusters list
```

2. Test S3-compatible storage.

```powershell
ChoboCli targets test-connection --id <target-id>
```

3. Create a small selector that includes one non-critical table, then run a manual backup.

```powershell
ChoboCli backup manual --policy-id <policy-id> --backup-type Full
ChoboCli backups wait --id <backup-id> --timeout-seconds 900 --poll-seconds 5
ChoboCli backups show --id <backup-id>
```

4. Verify that schema is visible.

```powershell
ChoboCli schema show --backup-id <backup-id>
```

5. Check that the schedule appears in the dashboard projection and that logs and audits were written.

```powershell
ChoboCli dashboard --next-hours 24
ChoboCli logs show --last 50
ChoboCli audit show --last 50
```

If any step fails, fix it before enabling production-wide schedules.