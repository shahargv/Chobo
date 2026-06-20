DROP DATABASE IF EXISTS redistribute_subset_source ON CLUSTER {source.ClusterName} SYNC;
CREATE DATABASE redistribute_subset_source ON CLUSTER {source.ClusterName} ENGINE = Atomic;

CREATE TABLE redistribute_subset_source.orders_local ON CLUSTER {source.ClusterName}
(
    id UInt64,
    shard UInt8,
    name String
)
ENGINE = MergeTree
ORDER BY id;

DROP DATABASE IF EXISTS redistribute_subset_replicated_source ON CLUSTER {source.ClusterName} SYNC;
CREATE DATABASE redistribute_subset_replicated_source ON CLUSTER {source.ClusterName} ENGINE = Replicated('/clickhouse/databases/redistribute_subset_replicated_source', '{shard}', '{replica}');

CREATE TABLE redistribute_subset_replicated_source.orders_local
(
    id UInt64,
    shard UInt8,
    name String
)
ENGINE = MergeTree
ORDER BY id;


