# Upgrading Chobo

Treat a Chobo upgrade as a metadata-database upgrade. Keep the server, web application, and CLI on the same release, and plan a short maintenance window so no backup, restore, retention, or garbage-collection work is active while files and containers are replaced.

## Before the upgrade

1. Check the current server version with `ChoboCli server version` or `GET /api/v1/server/version`.
2. Wait for active backup and restore runs to finish. Do not start new runs during the maintenance window.
3. Create both exports and copy them away from the Chobo host:

   ```powershell
   ChoboCli config export --output .\chobo-config-before-upgrade.json
   ChoboCli data export --output .\chobo-data-before-upgrade.json
   ```

4. Stop ChoboServer, then copy the complete configured Chobo data directory, including `chobo.db` and any SQLite self-backups.
5. Separately copy the complete `<data-directory>/secrets/aes-keys` directory. Preserve file names, contents, ownership, and permissions.
6. Verify the database, exports, and AES-key copy exist, are non-empty, and can be read from the rollback location.
7. Record the currently deployed server and CLI image tags or standalone binary versions.

### Using the web GUI

1. Sign in and open **Dashboard**. Wait until active backup and restore work has finished; use the queue and backup/restore history links to investigate anything still running.
2. Open **Import/Export** and select **Export config**, then **Export data**. Save both downloaded JSON files outside the Chobo data directory.
3. Review **Clusters**, **Backup targets**, **Policies**, **Schedules**, and **Backups** so you can compare them after the upgrade.
4. After stopping the server, copy the configured data directory and separately copy its `secrets/aes-keys` directory as described above.

`CHOBO_ENCRYPTION_KEY_BASE64` is not a substitute for the AES-key directory. Backup-password records refer to a specific AES key ID (the key file name), including historical IDs left by key rotation. Losing those files can make otherwise healthy password-protected backups impossible to restore.

## Docker upgrade

Use pinned release tags rather than `latest`.

1. Stop the existing Chobo services after the pre-upgrade copies complete.
2. Change the ChoboServer, web, and CLI image references to the same target release.
3. Keep the existing data-directory volume and secret configuration mounted at the same paths.
4. Pull the pinned images and start ChoboServer.
5. Watch startup logs until database migration and server startup complete. Chobo rejects a database whose schema is newer than the running server.

Use the same Docker Compose commands and service names documented for your installation. Do not initialize a new data volume during an upgrade.

## Standalone binary upgrade

1. Stop the ChoboServer service.
2. Preserve the existing configuration and data directory.
3. Replace the server, web assets, and CLI with files from the same target release.
4. Start ChoboServer and inspect its startup log for schema-migration errors.

Database schema upgrades run on server startup. New optional policy and backup metadata fields are additive; older policies and backup shards retain their previous unprotected behavior when those fields are absent.

## Post-upgrade checks

Run these checks before re-enabling normal schedules:

- `ChoboCli server version` reports the expected product, API, export, and schema versions.
- The CLI does not report a server compatibility error.
- Login succeeds and clusters, targets, policies, schedules, and backup history are present.
- Encrypted backups show a healthy lock/check indicator. Investigate any lock warning before relying on that backup for restore.
- ClickHouse and backup-target connection tests succeed.
- A small policy backup succeeds, followed by a restore into a scratch table.
- Audit and application logs contain no unexpected migration or credential errors.

### Using the web GUI

1. Open **Dashboard** and confirm the server is online.
2. Open **Clusters** and **Backup targets**, inspect the existing entries, and run each available connection test.
3. Open **Policies** and **Schedules** and confirm that names, selectors, optional password/compression settings, and schedules are present.
4. Open **Backups**. Check that encrypted backups show a healthy key indicator and that compressed backups report their compression method separately.
5. Create a small manual backup from a test policy, wait for **Succeeded**, then open **Restores** and restore it to a scratch database/table.
6. Review **Logs and audits** for migration, credential, or storage errors before re-enabling schedules.

Example smoke test:

```powershell
ChoboCli dashboard --next-hours 24
ChoboCli clusters test-connection --id <cluster-id>
ChoboCli targets test-connection --id <target-id>
ChoboCli backup manual --policy-id <small-policy-id> --backup-type Full
ChoboCli backups wait --id <backup-id> --timeout-seconds 900 --poll-seconds 5
```

## Missing AES keys after import or recovery

Imports preserve encrypted policy and table-shard password records even when their AES key is not present. The import succeeds with a warning that lists missing or invalid key IDs. The GUI shows a warning lock in backup history and a red X beside the AES key ID in backup details; CLI JSON exposes the same availability state.

An affected backup remains visible and can still expire or be deleted by garbage collection, but restore and incremental parent selection do not use it. Copy the named key file from the trusted pre-upgrade key backup into `<data-directory>/secrets/aes-keys`, restore the original restrictive ownership and permissions, and refresh the backup view. Chobo watches this directory and also rescans it periodically, so the indicator should become healthy without rewriting or re-importing database rows.

In the GUI, open **Import/Export** after restoring the file and refresh **Backups**. The warning lock should change to the available-key indicator; open the backup details drawer to verify each affected shard. If the warning remains, check the exact key ID, file name, permissions, and server logs.

Do not invent an empty file or rename a different key. A malformed file is reported as unavailable just like a missing key.

## Rollback

Stop the upgraded server before rollback. Restore the previous binaries or pinned images, the matching pre-upgrade `chobo.db`/data-directory copy, and the matching `secrets/aes-keys` directory as one set. A server may reject a database created by a newer release, so replacing only the executable is not a safe rollback.

After rollback, repeat the version, login, configuration, connection, small-backup, and scratch-restore checks before schedules resume.
