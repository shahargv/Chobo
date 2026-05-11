DROP DATABASE IF EXISTS smoke_cluster1_db ON CLUSTER {cluster1.ClusterName} SYNC;
CREATE DATABASE IF NOT EXISTS smoke_cluster1_db ON CLUSTER {cluster1.ClusterName} ENGINE = Replicated('/clickhouse/databases/smoke_cluster1_db', '{shard}', '{replica}');

CREATE TABLE IF NOT EXISTS smoke_cluster1_db.smoke_cluster1_orders
(
    Id UInt32,
    PartitionNumber UInt32,
    Name String,
    Amount UInt32
)
ENGINE = ReplicatedMergeTree
PARTITION BY PartitionNumber
ORDER BY (PartitionNumber, Id);
