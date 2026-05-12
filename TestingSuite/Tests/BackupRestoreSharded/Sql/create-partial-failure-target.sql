DROP DATABASE IF EXISTS backup_sharded_partial SYNC;
CREATE DATABASE backup_sharded_partial ENGINE = Atomic;

CREATE TABLE backup_sharded_partial.orders_local
(
    id UInt64,
    unexpected String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_sharded_partial.orders_local (id, unexpected) VALUES (700, 'bad-target');
