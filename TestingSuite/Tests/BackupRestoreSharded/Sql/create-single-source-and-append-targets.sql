DROP DATABASE IF EXISTS backup_single_source SYNC;
DROP DATABASE IF EXISTS backup_sharded_single SYNC;

CREATE DATABASE backup_single_source ENGINE = Atomic;
CREATE TABLE backup_single_source.orders_local
(
    id UInt64,
    shard UInt8,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_single_source.orders_local (id, shard, name) VALUES
    (201, 1, 'single-a'),
    (202, 1, 'single-b');

CREATE DATABASE backup_sharded_single ENGINE = Atomic;
CREATE TABLE backup_sharded_single.append_orders
(
    id UInt64,
    shard UInt8,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_sharded_single.append_orders (id, shard, name) VALUES
    (900, 9, 'existing');
