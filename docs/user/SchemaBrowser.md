# Schema Browser

The schema browser shows DDL captured during backups. It is useful when you need to inspect table definitions, compare backup points, or plan a restore.

Schema browsing reads Chobo metadata only. It does not connect to ClickHouse or S3.

## Web GUI

Open **Schema Browser**.

![Schema browser showing a selected backup and captured CREATE TABLE SQL](assets/schema/schema-browser-table-sql.png)

Use it to choose a backup, browse databases and tables, view captured `CREATE TABLE` SQL, and export all schema SQL or one database.

## CLI

List backups with retained schema:

```powershell
ChoboCli schema backups
```

Example output:

```json
[
  {
    "backupId": "6b63350a-7073-49a3-884e-f77ee7f58433",
    "policyName": "sales-nightly",
    "createdAt": "2026-06-21T02:00:00Z",
    "databaseCount": 2,
    "tableCount": 12
  }
]
```

Show schema for one backup:

```powershell
ChoboCli schema show --backup-id 6b63350a-7073-49a3-884e-f77ee7f58433
```

Show one table:

```powershell
ChoboCli schema show --backup-id 6b63350a-7073-49a3-884e-f77ee7f58433 --database sales --table orders
```

Export SQL:

```powershell
ChoboCli schema export --backup-id 6b63350a-7073-49a3-884e-f77ee7f58433 --database sales
```

Example output:

```sql
CREATE DATABASE IF NOT EXISTS sales;

CREATE TABLE sales.orders
(
    `order_id` UInt64,
    `customer_id` UInt64,
    `created_at` DateTime,
    `amount` Decimal(18, 2)
)
ENGINE = MergeTree
ORDER BY order_id;
```

## What Schema Capture Means

For schema+data backups, Chobo captures DDL for every selected table. Data backup is submitted only for ClickHouse `*MergeTree` engines. Other selected engines keep schema metadata and are marked as not data-backed.

For schema-only backups, Chobo captures DDL and does not write objects to S3.

For cluster mode, schema-only capture queries every source node and deduplicates by `database.table`.

## During Restore Planning

Before a restore, use the schema browser to answer:

- Which database and table names are in the backup?
- Does the target table already exist?
- If appending, do the source and target columns match?
- Is this a schema-only table or a data-backed table?
- Was the backup full, incremental, succeeded, or partially succeeded?
