DROP DATABASE IF EXISTS backup_sharded_partial SYNC;
CREATE DATABASE backup_sharded_partial ENGINE = Atomic;

CREATE TABLE backup_sharded_partial.orders_local
(
    id UInt64,
    shard UInt8,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_sharded_partial.orders_local (id, shard, name) VALUES (600, 6, 'existing-good');
