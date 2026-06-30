DROP DATABASE IF EXISTS backup_node_down_source SYNC;
CREATE DATABASE backup_node_down_source ENGINE = Atomic;

CREATE TABLE backup_node_down_source.orders_local
(
    id UInt64,
    shard UInt32,
    name String
)
ENGINE = ReplicatedMergeTree('/clickhouse/tables/{shard}/backup_node_down_source/orders_local', '{replica}')
ORDER BY id;
