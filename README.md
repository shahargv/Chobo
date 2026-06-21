# Chobo

Chobo is for DBAs who run ClickHouse clusters and need backups they can schedule, inspect, and restore without building their own orchestration. Add your ClickHouse cluster, add your S3-compatible backup storage, create a backup policy, then run it manually or on a schedule from the web UI or CLI.

## Screenshots

![Dashboard showing running work and upcoming schedules](docs/assets/readme/dashboard.png)

![Backup details with table, shard, log, and audit context](docs/assets/readme/backup-detail.png)

![Schema browser showing captured ClickHouse DDL](docs/assets/readme/schema-browser.png)

![Restore workflow and restore details](docs/assets/readme/restore-workflow.png)

## Start Using Chobo

Pull the Docker images:

```bash
docker pull shahargv/chobo:server-latest
docker pull shahargv/chobo:cli-latest
```

Then follow:

- [Production setup](docs/ProductionSetup.md)
- [Configuration](docs/Configuration.md)
- [Setting up backups](docs/Backups.md)
- [Restoring](docs/Restoring.md)
