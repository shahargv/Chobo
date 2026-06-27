# Troubleshooting

Start with the run record, then move outward to logs, audits, ClickHouse, and S3.

## Quick Health Checks

```powershell
ChoboCli dashboard --next-hours 12
ChoboCli metrics show
ChoboCli clusters list
ChoboCli targets list
ChoboCli policies list
ChoboCli schedules list
```

HTTP health:

```bash
curl -fsS https://chobo.example.com/health
```

## Backup Failed

Inspect the backup:

```powershell
ChoboCli backups show --id <backup-id>
ChoboCli backups progress --id <backup-id>
ChoboCli logs show --last 500
ChoboCli audit show --last 200
```

Check:

- Does `failureReason` explain the run failure?
- Did one table or one shard fail while others succeeded?
- Does the ClickHouse user have backup permissions?
- Can ClickHouse nodes reach the S3 endpoint?
- Is the bucket or path prefix correct?
- Does S3 allow writes under the configured prefix?

If a backup is `PartiallySucceeded`, the successful shards may still be useful for restore. Review the shard list before deleting anything.

## Scheduled Backup Did Not Run

```powershell
ChoboCli schedules list
ChoboCli dashboard --next-hours 24
ChoboCli audit show --last 200
ChoboCli logs show --last 500
```

Check:

- Is the schedule enabled?
- Is the cron expression valid for Quartz-style syntax?
- Is the timezone correct?
- Did Chobo start after the missed-run grace period?
- Is the backup/restore queue full?

## Restore Failed

```powershell
ChoboCli restores show --id <restore-id>
ChoboCli logs show --last 500
ChoboCli audit show --last 200
```

Check:

- Is the backup status `Succeeded` or `PartiallySucceeded`?
- Does the target cluster exist and pass connection tests?
- Does the selected restore layout match the target topology?
- Does the target table already exist when you are not using `--append`?
- Does the target table exist when you are using `--append`?
- If `--allow-schema-mismatch` was used, did Chobo warn about omitted columns?


## Failure Diagnosis Matrix

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| Cluster connection test fails | Wrong host, port, TLS setting, credentials, or ClickHouse permissions | Run `ChoboCli clusters test-connection --id <cluster-id>`, verify the ClickHouse HTTP(S) port, update credentials, and check ClickHouse grants. |
| S3 target test succeeds but backup fails | ChoboServer can reach S3, but ClickHouse nodes cannot | Test the S3 endpoint from a ClickHouse node. |
| Backup fails with access denied | S3 identity cannot write, list, read, or delete under the bucket/prefix | Fix the S3 policy for the configured bucket and prefix, then rerun a small backup. |
| TLS or certificate error | Endpoint requires HTTPS or uses an untrusted certificate | Verify ClickHouse `--tls`, reverse proxy certificates, and S3 endpoint trust from both ChoboServer and ClickHouse. |
| Run is stuck running | ClickHouse async operation is still running, cannot be polled, or queue workers are blocked | Inspect `backups show`, `restores show`, logs, and ClickHouse `system.backups` using the recorded operation id. |
| One shard failed | Source or target shard node is down, unreachable, or has a local ClickHouse error | Inspect shard-level `host`, `port`, `sourceShardNumber`, `targetShardNumber`, and `error`; repair the node before retrying. |
| Manifest recovery finds fewer backups than expected | Manifests were deleted, path prefix is wrong, or scan root is too narrow | Confirm the S3 prefix and try `backups recover --backup-path` for a known manifest path. |
| Retention or garbage collection keeps failing | ChoboServer cannot delete S3 objects or credentials are invalid | Run `ChoboCli gc status`, inspect `deletionError`, update S3 credentials, then run `ChoboCli gc run`. |
| Connection tests fail after moving Chobo | Encryption key changed or credentials were not imported | Restore the original `CHOBO_ENCRYPTION_KEY_BASE64` or re-enter ClickHouse and S3 credentials. |
| CLI reports version or compatibility errors | CLI and server images do not match | Use the CLI image released with the server image and check `/api/v1/server/version`. |
| Queue is full | Too many backup/restore requests are queued | Wait for active work, cancel wrong requests, or increase `Chobo:BackupRestore:QueueCapacity` after checking capacity. |

## Cancellation During Incidents

Cancel only when a request is clearly wrong or dangerous to continue.

```powershell
ChoboCli backups cancel --id <backup-id>
ChoboCli restores cancel --id <restore-id>
```

After cancellation, inspect the run. Already-started ClickHouse async operations or temporary restore objects may still need review.

## Chobo Cannot Read Stored Credentials

If connection tests fail after a server move or key change, verify `CHOBO_ENCRYPTION_KEY_BASE64`.

Changing the key makes old encrypted credentials unreadable. Re-enter credentials:

```powershell
ChoboCli clusters update-credentials --id <cluster-id> --username <user> --password <password>
ChoboCli targets update-s3 --id <target-id> --name prod-s3 --endpoint https://s3.example.com --bucket chobo-backups --access-key <key> --secret-key <secret>
```

## Local SQLite Metadata Was Lost

If the data directory still has Chobo initialization state but `chobo.db` is missing, Chobo starts with a fresh SQLite database and logs a warning. It does not scan S3 automatically.

Recovery flow:

```powershell
ChoboCli server auth --server-url http://localhost:8080 --access-token <fresh-init-token>
ChoboCli targets add-s3 --name recovery-s3 --endpoint https://s3.example.com --bucket chobo-backups --path-prefix prod --access-key <key> --secret-key <secret>
ChoboCli backups recover --target-id <new-target-id> --scan-root backups
ChoboCli clusters update-credentials --id <recovered-cluster-id> --username <clickhouse-user> --password <clickhouse-password>
```

For one known manifest path:

```powershell
ChoboCli backups recover --target-id <new-target-id> --backup-path backups/policy-<policy-id>/_chobo/<backup-id>.json
```

Storage manifests do not contain ClickHouse passwords or S3 secret keys. If recovery finds that some declared S3 data paths are already missing, it imports that backup as `PartiallySucceeded` so remaining data can still be managed.

## CLI Version Or Authentication Fails

```powershell
ChoboCli server auth --server-url https://chobo.example.com --access-token <token>
curl -H "Authorization: Bearer <token>" https://chobo.example.com/api/v1/server/version
```

Check:

- the server URL points to ChoboServer, not ClickHouse;
- the token is active;
- the CLI image or binary matches the server version;
- TLS termination and proxy forwarding are working.
