# Chobo

Chobo is a backup and restore orchestration tool for teams running ClickHouse.

ClickHouse already provides the low-level `BACKUP` and `RESTORE` primitives. Chobo adds the operational layer around them: configured clusters and storage targets, repeatable policies, schedules, run history, schema browsing, restore workflows, audit records, and recovery metadata.

## How Chobo Works

Chobo has a backend service, a command-line tool, and a web GUI.

DBAs define ClickHouse sources, backup storage, table selection rules, schedules, and retention settings. Chobo then runs backups, tracks progress, records audit and log entries, and keeps the metadata needed to inspect or restore from previous runs.

Restores are handled as planned operations rather than one-off commands. The UI and CLI help choose the backup, target cluster, tables, shard layout, and restore mode, then expose the result with table and shard-level status.

## Screenshots

![Dashboard showing running work and upcoming schedules](docs/assets/readme/dashboard.png)

The dashboard gives operators a quick view of active backup work, configured schedules, and upcoming runs.

![Backup details with table, shard, log, and audit context](docs/assets/readme/backup-detail.png)

Backup details expose run status, selected tables, shard outcomes, related logs, and audit entries.

![Schema browser showing captured ClickHouse DDL](docs/assets/readme/schema-browser.png)

Schema browsing reads Chobo metadata, so operators can inspect captured DDL without reconnecting to ClickHouse or S3.

![Restore workflow and restore details](docs/assets/readme/restore-workflow.png)

Restore pages keep the recovery workflow explicit: source backup, target cluster, table scope, shard layout, and final run status.

## Features

- Define backup policies once and run them manually or on a schedule.
- Protect single-node and clustered ClickHouse deployments from the same tool.
- Select tables with include and exclude rules instead of maintaining long command lists.
- Use full, incremental, and schema-only backups depending on the recovery need.
- See backup and restore progress down to the table and shard level.
- Restore an entire backup, one table, selected shards, or schema only.
- Append into existing tables when that is the intended recovery path.
- Keep retention and failed-backup cleanup visible and auditable.
- Inspect captured table DDL without connecting back to ClickHouse.
- Recover Chobo metadata from backup manifests stored in S3-compatible storage.
- Use either the GUI for interactive operations or the CLI for automation.
- Review audit records and logs for configuration changes, scheduled work, and manual actions.

## Quick Start

Pull the Docker images from [Docker Hub](https://hub.docker.com/r/shahargv/chobo):

```bash
docker pull shahargv/chobo:server-latest
docker pull shahargv/chobo:cli-latest
```

For setup and operations, start with:

- [Production setup](docs/ProductionSetup.md)
- [Configuration](docs/Configuration.md)
- [Setting up backups](docs/Backups.md)
- [Restoring](docs/Restoring.md)

## Typical Workflow

1. Configure a ClickHouse source cluster.
2. Configure an S3-compatible backup target.
3. Create a selector that includes and excludes the tables you want protected.
4. Save a backup policy with retention rules.
5. Add schedules, or run manual backups when needed.
6. Inspect backup results, captured schema, logs, and audit records.
7. Start restores from known recovery points and track table or shard-level progress.

## Documentation

The main documentation index is [docs/README.md](docs/README.md). Useful entry points:

- [Backup lifecycle management](docs/BackupLifecycleManagement.md)
- [System test suite](docs/SystemTestSuite.md)
- [Release process](docs/Releasing.md)

## Security Notes

Chobo stores ClickHouse and S3 credentials encrypted with the configured server key. Credentials are write-only in API and CLI output. Import/export envelopes do not carry raw access tokens, decrypted credentials, or local AES key material; imported ClickHouse and S3 credentials must be re-entered so they are encrypted by the current server.
