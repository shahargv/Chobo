# Logs And Audits

Use logs to understand what Chobo tried to do. Use audits to understand who changed state or what background action changed state.

## Application Logs

GUI: open **Logs**.

CLI:

```powershell
ChoboCli logs show --last 500
```

Example output:

```json
{
  "items": [
    {
      "id": 101,
      "timestamp": "2026-06-21T02:00:04Z",
      "level": "Information",
      "category": "ChoboServer.Application.BackupRunnerService",
      "message": "Backup run started",
      "exception": null
    },
    {
      "id": 124,
      "timestamp": "2026-06-21T02:04:18Z",
      "level": "Information",
      "category": "ChoboServer.Application.BackupRunnerService",
      "message": "Backup run completed with status Succeeded",
      "exception": null
    }
  ],
  "offset": 0,
  "limit": 500,
  "totalCount": 2
}
```

Filter by time:

```powershell
ChoboCli logs show --start-time 2026-06-21T02:00:00Z --end-time 2026-06-21T02:10:00Z
```

Clear old log entries:

```powershell
ChoboCli logs clear --before 2026-05-01T00:00:00Z
```

## Audit Records

GUI: open **Audit**.

CLI:

```powershell
ChoboCli audit show --last 200
```

Example output:

```json
{
  "items": [
    {
      "id": 88,
      "timestamp": "2026-06-21T01:55:12Z",
      "actorUserId": "d1dc5a8a-5da1-40fa-b20d-56eac015513d",
      "actorName": "admin",
      "action": "created",
      "entityType": "BackupPolicy",
      "entityId": "4e97f04c-b1ed-4766-9a3b-5162d02f0475",
      "details": {
        "name": "sales-nightly",
        "contentMode": "SchemaAndData"
      }
    },
    {
      "id": 103,
      "timestamp": "2026-06-21T02:04:18Z",
      "actorUserId": null,
      "actorName": "system",
      "action": "succeeded",
      "entityType": "Backup",
      "entityId": "6b63350a-7073-49a3-884e-f77ee7f58433",
      "details": {
        "policyName": "sales-nightly",
        "tableCount": 12,
        "shardCount": 36
      }
    }
  ],
  "offset": 0,
  "limit": 200,
  "totalCount": 2
}
```

Audit actors:

- user/API/CLI actions include the authenticated user id and user name;
- scheduled work, startup maintenance, retention, import, and local maintenance use the reserved `system` actor.

Clear old audit entries only if your operational policy allows it:

```powershell
ChoboCli audit clear --before 2026-01-01T00:00:00Z
```

## What To Check During Incidents

For backup failures:

```powershell
ChoboCli backups show --id <backup-id>
ChoboCli backups progress --id <backup-id>
ChoboCli logs show --last 500
ChoboCli audit show --last 200
```

For restore failures:

```powershell
ChoboCli restores show --id <restore-id>
ChoboCli logs show --last 500
ChoboCli audit show --last 200
```

Look for `failureReason`, table-level and shard-level `error` values, ClickHouse operation ids, selected source and target nodes, and audit actions such as `shard-failed`, `table-partially-succeeded`, `failed`, or `partially-succeeded`.

## Retention Settings

Chobo can remove old log and audit entries through configuration:

```json
{
  "Chobo": {
    "DataRetention": {
      "Interval": "01:00:00",
      "LogsBefore": null,
      "AuditsBefore": null,
      "DeletedBackupRestoreRecordRetention": "90.00:00:00"
    }
  }
}
```

For production, keep enough history to investigate backup failures, restore decisions, and access-token changes. Chobo also hard-deletes backup and restore history for backups that completed deletion successfully after `DeletedBackupRestoreRecordRetention`, defaulting to 90 days after `deletedAt`.
