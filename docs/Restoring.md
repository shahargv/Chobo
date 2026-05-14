# Restoring

Restore operations are created from an existing backup and a target ClickHouse cluster. Chobo queues the restore, runs ClickHouse async restore operations, and records run, table, and shard-level status.

## Restore Requirements

A backup can be restored only when its status is:

- `Succeeded`
- `PartiallySucceeded`

Deleted and delete-pending backups cannot be restored.

The target cluster must be configured in Chobo:

```powershell
ChoboCli clusters list
```

If the original backup is sharded, Chobo restores only source shards that completed successfully.

## Basic Restore

Restore all tables into the same database and table names on the target cluster:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id>
```

Wait for completion:

```powershell
ChoboCli restores wait --id <restore-id> --timeout-seconds 300 --poll-seconds 2
```

Inspect details:

```powershell
ChoboCli restores show --id <restore-id>
ChoboCli restores list
```

## Restore One Table

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --database sales --table orders
```

Restore one table to a new name:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --database sales --table orders --target-database restore_sales --target-table orders_copy
```

Target database/table overrides are supported only when restoring a single table.

## Append Restore

Append into an existing compatible table:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --database sales --table orders --append
```

Append requires the target table to already exist.

By default, Chobo rejects a target table with a different schema. To append common columns despite schema differences:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --database sales --table orders --append --allow-schema-mismatch
```

When schema mismatch is allowed, Chobo inserts only columns that exist in both the backup schema and the target table. The restore record includes a warning.

## Restore Layouts

Use `--layout` to control source-shard to target-shard mapping.

`preserve` is the default:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout preserve
```

Preserve maps source shard N to target shard N. It requires matching source and target shard counts.

`redistribute` maps selected source shards across target shards in target shard order:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout redistribute
```

Use this when restoring from one shard count to another.

`single-node` restores selected source shards through the first target node:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout single-node
```

Use this for sharded-to-standalone restore or single-shard extraction.

## Source And Target Shard Controls

Restore only one source shard:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout single-node --source-shard 1
```

Force selected source shards into a target shard:

```powershell
ChoboCli restore initiate --backup-id <backup-id> --target-cluster-id <cluster-id> --layout preserve --target-shard 2
```

Shard numbers must be positive. If `--target-shard` is provided, Chobo verifies that the target shard exists.

## Existing Target Tables

Without `--append`, Chobo fails when the target table already exists.

With `--append`, Chobo fails when the target table does not exist.

For schema-only backup tables, Chobo creates the target table from the stored schema when it does not exist and marks the restore table as `SCHEMA_ONLY`.

## Sharded Restore Mechanics

For sharded restores, Chobo:

- Plans one restore shard task for each succeeded backup shard selected by the request.
- Creates the target database if needed.
- Uses temporary restore tables when appending or when multiple source shards flow into one logical target table.
- Runs ClickHouse `RESTORE TABLE ... FROM S3(...) ASYNC`.
- Polls `system.backups` for operation status.
- Inserts from temporary tables into the final target table when needed.
- Drops temporary tables after successful insertion.

The restore result includes per-shard target host, target shard number, restore table name, layout role, ClickHouse operation id, status, warning, and error.

## Failure Handling

Run and table statuses can be `PartiallySucceeded`. This happens when at least one required shard succeeds and at least one fails.

Inspect:

```powershell
ChoboCli restores show --id <restore-id>
ChoboCli audit show --last 200
ChoboCli logs show --last 500
```

Useful audit actions include:

- `shard-failed`
- `table-partially-succeeded`
- restore-level `partially-succeeded`
- restore-level `failed`

Restore records also expose `failureReason`, and each table or shard can expose its own `error`.

