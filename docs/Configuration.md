# Configuration

ChoboServer reads configuration from `appsettings.json`, optional environment-specific appsettings files, `CHOBO_APPSETTINGS_PATH`, environment variables, and command-line arguments.

Runtime services use typed options under `ChoboServer/Options`.

## Configuration Sources

Default appsettings:

```text
ChoboServer/appsettings.json
ChoboServer/appsettings.Development.json
```

Optional external settings file:

```powershell
$env:CHOBO_APPSETTINGS_PATH = "C:\etc\chobo\appsettings.Production.json"
```

When `CHOBO_APPSETTINGS_PATH` is set, Chobo also adds standard environment variables and command-line configuration.

## Core Settings

```json
{
  "Chobo": {
    "DataDirectory": "data",
    "EncryptionKeyBase64": null,
    "Init": {
      "AdminUser": null,
      "AccessToken": null
    }
  }
}
```

`Chobo:DataDirectory` is where Chobo stores `chobo.db` and the SQLite-backed application logs. Use persistent storage in production.

`Chobo:EncryptionKeyBase64` is the key used to protect stored ClickHouse and S3 credentials. Use a stable 32-byte key encoded with Base64. Do not rotate it unless you also re-encrypt or recreate stored credentials.

`Chobo:Init:AdminUser` and `Chobo:Init:AccessToken` bootstrap the first user and access token when the database is initialized.

Environment aliases:

```text
CHOBO_DATA_DIRECTORY
CHOBO_ENCRYPTION_KEY_BASE64
CHOBO_INIT_ADMIN_USER
CHOBO_INIT_ACCESS_TOKEN
```

## Server Binding

Use standard ASP.NET Core settings:

```powershell
$env:ASPNETCORE_URLS = "http://0.0.0.0:8080"
$env:ASPNETCORE_ENVIRONMENT = "Production"
```

Terminate TLS at your ingress, reverse proxy, load balancer, or hosting platform.

## Backup And Restore Worker Settings

```json
{
  "Chobo": {
    "BackupRestore": {
      "MaxDop": 3,
      "QueueCapacity": 100,
      "SchedulerInterval": "00:01:00",
      "SchedulerMissedRunGracePeriod": "00:05:00",
      "PollInterval": "00:00:02"
    }
  }
}
```

- `MaxDop`: default maximum parallel table operations for backup and restore. A cluster-level `--backup-restore-maxdop` overrides this for that cluster.
- `QueueCapacity`: bounded in-memory queue size for backup and restore requests.
- `SchedulerInterval`: how often the scheduler checks for due schedules.
- `SchedulerMissedRunGracePeriod`: default maximum lateness for a scheduled run when the schedule itself does not specify one.
- `PollInterval`: how often Chobo polls ClickHouse `system.backups` for async operation status.

Environment aliases:

```text
CHOBO_BACKUP_RESTORE_MAX_DOP
CHOBO_BACKUP_RESTORE_QUEUE_CAPACITY
CHOBO_BACKUP_RESTORE_SCHEDULER_INTERVAL
CHOBO_BACKUP_RESTORE_SCHEDULER_MISSED_RUN_GRACE_PERIOD
CHOBO_BACKUP_RESTORE_POLL_INTERVAL
```

## Retention And Cleanup Settings

```json
{
  "Chobo": {
    "RetentionManagement": {
      "Interval": "01:00:00",
      "MaxDop": 2
    },
    "BackupsGarbageCollector": {
      "Interval": "01:00:00",
      "MaxDop": 2
    }
  }
}
```

- `RetentionManagement:Interval`: how often expired successful backups and manual delete requests are processed.
- `RetentionManagement:MaxDop`: maximum parallel backup cleanup operations for retention/manual deletion.
- `BackupsGarbageCollector:Interval`: how often failed or partially succeeded backups are checked for garbage collection.
- `BackupsGarbageCollector:MaxDop`: maximum parallel failed-backup cleanup operations.

Use normal .NET environment variable binding for settings without explicit aliases:

```text
Chobo__RetentionManagement__Interval=00:30:00
Chobo__RetentionManagement__MaxDop=2
Chobo__BackupsGarbageCollector__Interval=00:30:00
Chobo__BackupsGarbageCollector__MaxDop=2
```

## Log And Audit Retention Settings

```json
{
  "Chobo": {
    "DataRetention": {
      "Interval": "01:00:00",
      "LogsBefore": null,
      "AuditsBefore": null
    }
  }
}
```

- `Interval`: how often the data-retention background service runs.
- `LogsBefore`: when set, application log entries before this timestamp are removed.
- `AuditsBefore`: when set, audit entries before this timestamp are removed.

Environment aliases:

```text
CHOBO_DATA_RETENTION_INTERVAL
CHOBO_DATA_RETENTION_LOGS_BEFORE
CHOBO_DATA_RETENTION_AUDITS_BEFORE
```

CLI commands are also available:

```powershell
ChoboCli logs clear --before 2026-05-01T00:00:00Z
ChoboCli audit clear --before 2026-05-01T00:00:00Z
```

## Serilog

Default production logging writes to console and rolling files:

```json
{
  "Serilog": {
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "logs/chobo-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 14
        }
      }
    ]
  }
}
```

Chobo also writes application logs into SQLite through its application log sink. Operators can inspect those with:

```powershell
ChoboCli logs show --last 500
```

## Test Hooks

`Chobo:TestHooks:Enabled` and `CHOBO_TEST_HOOKS_ENABLED` are for test scenarios. Leave test hooks disabled in production.

## Configuration Example

```json
{
  "AllowedHosts": "*",
  "Chobo": {
    "DataDirectory": "/var/lib/chobo",
    "EncryptionKeyBase64": "<base64-32-byte-key>",
    "BackupRestore": {
      "MaxDop": 3,
      "QueueCapacity": 100,
      "SchedulerInterval": "00:01:00",
      "SchedulerMissedRunGracePeriod": "00:05:00",
      "PollInterval": "00:00:02"
    },
    "RetentionManagement": {
      "Interval": "01:00:00",
      "MaxDop": 2
    },
    "BackupsGarbageCollector": {
      "Interval": "01:00:00",
      "MaxDop": 2
    },
    "DataRetention": {
      "Interval": "01:00:00",
      "LogsBefore": null,
      "AuditsBefore": null
    }
  }
}
```

