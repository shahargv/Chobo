# Chobo Documentation

Chobo is a backup and restore service for ClickHouse. It stores its own metadata in SQLite, protects configured credentials, and uses ClickHouse `BACKUP` and `RESTORE` operations against S3-compatible storage.

Start here:

- [Production setup](ProductionSetup.md)
- [Configuration](Configuration.md)
- [Setting up backups](Backups.md)
- [Restoring](Restoring.md)
- [Backup lifecycle management](BackupLifecycleManagement.md)

Additional local-development material:

- [Local debugging instructions](DebuggingInstructions.MD)

