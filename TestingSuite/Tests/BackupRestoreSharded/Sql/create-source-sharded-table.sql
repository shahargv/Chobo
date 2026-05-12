DROP DATABASE IF EXISTS backup_sharded_source ON CLUSTER {source.ClusterName} SYNC;
CREATE DATABASE backup_sharded_source ON CLUSTER {source.ClusterName} ENGINE = Atomic;

CREATE TABLE backup_sharded_source.orders_local ON CLUSTER {source.ClusterName}
(
    id UInt64,
    shard UInt8,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_sharded_source.orders_local (id, shard, name) VALUES (1, 1, 's1-a'), (2, 1, 's1-b');
