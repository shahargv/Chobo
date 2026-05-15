CREATE DATABASE IF NOT EXISTS metadata_recovery_sharded ON CLUSTER {sourceSharded.ClusterName} ENGINE = Atomic;
CREATE TABLE IF NOT EXISTS metadata_recovery_sharded.orders_local ON CLUSTER {sourceSharded.ClusterName}
(
    id UInt64,
    shard UInt32,
    label String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO metadata_recovery_sharded.orders_local VALUES (1, 1, 'sharded-full-s1');
