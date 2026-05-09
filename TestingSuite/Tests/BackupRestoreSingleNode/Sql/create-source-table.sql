DROP DATABASE IF EXISTS backup_single_source SYNC;
CREATE DATABASE IF NOT EXISTS backup_single_source ENGINE = Atomic;

CREATE TABLE backup_single_source.source_orders
(
    id UInt32,
    name String
)
ENGINE = MergeTree
ORDER BY id;
