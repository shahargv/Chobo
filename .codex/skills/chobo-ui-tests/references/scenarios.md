# Chobo UI Test Scenarios

All scenarios use Chrome through `scripts/run-ui-scenario.mjs`. Run `scripts/start-ui-env.ps1` first unless testing an already-running environment with `--base-url`.

## bootstrap

1. Open the published Chobo URL in a clean browser context.
2. Verify the first screen is installation, not token login.
3. Click install.
4. Capture the one-time initial access token from the visible token box.
5. Screenshot token-created state before leaving it.
6. Continue/reload to the login screen.
7. Sign in with the captured token.
8. Verify dashboard/app shell appears.
9. Verify `dev-static-access-token` and `static-test-token` were not used.
10. Verify anonymous `/api/v1/users` is rejected and install cannot be repeated.

## cluster

Prerequisite: bootstrap.

1. Open ClickHouse Clusters.
2. Create `ui-source` with `clickhouse-source:9000`, single instance, Max DOP 1.
3. Test the saved cluster and expect success feedback.
4. Create `ui-restore` with `clickhouse-restore:9000`, single instance, Max DOP 1.
5. Test the saved restore cluster and expect success feedback.
6. Edit `ui-source`, leave credentials unmodified, change a harmless value such as Max DOP if needed, save, and confirm persistence.

## storage

Prerequisite: bootstrap.

1. Open Backup Storage.
2. Create `ui-minio` with the MinIO values from `test-data.md`.
3. Test the storage target and expect success feedback.
4. Edit `ui-minio`, leave credentials unmodified, save, and confirm persistence.
5. Verify the secret key is not visible in the saved row or edit form unless modifying credentials.

## policy

Prerequisite: cluster and storage.

1. Open Policies.
2. Create `ui-orders-policy`.
3. Select `ui-source` and `ui-minio`.
4. Replace the default selector with Include exact `backup_single_source`.`source_orders`.
5. Confirm selector preview includes `backup_single_source.source_orders`.
6. Set retention minimums to 1 and 1.
7. Save, reopen, edit a field, save again, and confirm persistence.

## schedule-edit

Prerequisite: policy.

1. Open Schedules.
2. Create `ui-daily-full` for `ui-orders-policy`, Full, UTC, daily 02:00.
3. Verify cron preview validates and lists next runs.
4. Save, reopen, toggle enabled off/on or adjust minute, save, and confirm persistence.

## backup

Prerequisite: policy.

1. Execute `ui-orders-policy` now.
2. Open Backups and watch the run transition to Succeeded.
3. Open backup details.
4. Verify status, table list, shard/operation context, logs, and audit context are understandable.

## restore

Prerequisite: successful backup.

1. Open Restore wizard.
2. Choose the latest successful backup.
3. Choose `ui-restore` as target.
4. Use a valid single-node layout.
5. Select `backup_single_source.source_orders`.
6. Set target database `backup_single_restore` and target table `restored_orders`.
7. Review impact, execute, and wait for Succeeded in restore history.
8. Open restore details.
9. Verify restored rows with SQL.

## details

Prerequisite: successful backup and restore.

Open backup details and restore details directly. Confirm the records are understandable after a route reload and that related logs/audit are visible.

## logs-audit

Prerequisite: any mutating scenario.

Open Logs and Audit. Test recent records, search/filter affordances, time window behavior, and pagination. Confirm records exist for install, cluster/storage/policy/schedule changes, backup, and restore.

## failure

Use one intentionally bad value at a time, such as `http://backup-s3:9999` for storage or `missing-clickhouse:9000` for cluster. Verify the UI gives a clear failure message, remains recoverable, and screenshots/report capture the failure.
