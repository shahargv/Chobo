CREATE DATABASE IF NOT EXISTS backup_retention_source;
CREATE TABLE IF NOT EXISTS backup_retention_source.orders
(
    id UInt64,
    name String
)
ENGINE = MergeTree
ORDER BY id;
