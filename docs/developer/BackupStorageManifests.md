# Backup Storage Manifests

Chobo writes one storage-side metadata manifest for each schema+data backup. The manifest lets a fresh SQLite database recover backup metadata from S3-compatible storage without storing credentials in S3.

## Layout

Policy-backed backups use:

```text
backups/policy-<policy-id>/_chobo/<backup-id>.json
```

Ad hoc manual backups without a policy use:

```text
backups/manual/_chobo/<backup-id>.json
```

Scan recovery discovers only this layout. Legacy per-table or per-shard objects such as `<data-path>/_chobo/backup-metadata.v1.json` are ignored by scan recovery.

## Contents

The manifest contains the backup run, target settings without credentials, source cluster topology without credentials, policy and schedule metadata when present, schema definitions, table rows, shard rows, and `requiredStoragePaths`.

`requiredStoragePaths` is the list of S3 prefixes that must contain data for the recovered backup to represent a complete data backup:

- sharded data backups list each shard `S3Path`;
- single-node data backups list the table `S3Path`;
- schema-only backups do not write manifests.

Manifests must not include ClickHouse usernames/passwords, S3 access keys/secrets, encrypted credential blobs, or local AES key material.

## Recovery Behavior

`backups recover --scan-root backups` lists objects under the scan root and imports new-layout manifest objects. Duplicate backup ids are ignored after the first manifest seen in sorted object-path order.

Before import, recovery lists each `requiredStoragePaths` prefix. If every prefix has objects, the backup status and table/shard statuses are imported from the manifest. If any prefix is missing or empty, recovery still imports the backup as `PartiallySucceeded`, marks the affected table or shard rows failed with a recovery error, and records recovery errors in the result. This keeps remaining storage paths visible to lifecycle cleanup and diagnostics.

`backups recover --backup-path` expects a direct manifest object path, for example `backups/policy-<policy-id>/_chobo/<backup-id>.json`.

## Cleanup

Backup cleanup deletes recorded data directories first. After schema-reference cleanup, it deletes the manifest object as the final S3 object operation for that backup. If manifest deletion fails, cleanup leaves `DeletionError` set and does not mark the backup deleted, so the garbage collector can retry.
