DROP DATABASE IF EXISTS smoke_cluster2_db ON CLUSTER {cluster2.ClusterName} SYNC;
CREATE DATABASE IF NOT EXISTS smoke_cluster2_db ON CLUSTER {cluster2.ClusterName} ENGINE = Replicated('/clickhouse/databases/smoke_cluster2_db', '{shard}', '{replica}');

CREATE TABLE IF NOT EXISTS smoke_cluster2_db.smoke_cluster2_events
(
    Id UInt32,
    EventDate Date,
    Name String,
    Amount Decimal(10, 2)
)
ENGINE = ReplicatedMergeTree
ORDER BY (EventDate, Id);
