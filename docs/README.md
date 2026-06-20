# Chobo Documentation

Chobo is a backup and restore orchestration service for ClickHouse. It helps operators define what should be protected, run backups on demand or on schedule, track backup and restore history, and restore data into the right ClickHouse environment when needed.

Chobo is designed around operational workflows: configure storage and clusters, create backup policies, run or schedule backups, inspect results, and restore tables with enough status and audit information to understand what happened.

Start here:

- [Production setup](ProductionSetup.md)
- [Configuration](Configuration.md)
- [Setting up backups](Backups.md)
- [Restoring](Restoring.md)
- [Backup lifecycle management](BackupLifecycleManagement.md)
- [Releasing](Releasing.md)

Additional local-development material:

- [Local debugging instructions](DebuggingInstructions.MD)
- [System test suite](SystemTestSuite.md)
- [Codex development notes](CodexDevelopment.md)


Data export/import excludes audit entries and application logs, and imported ClickHouse/S3 credentials must be re-entered so they are encrypted with the current server key.
