CREATE TABLE IF NOT EXISTS {cluster1.DatabaseName}.{cluster1.TableName}
(
    Id UInt32,
    PartitionNumber UInt32,
    Name String,
    Amount UInt32
)
ENGINE = ReplicatedMergeTree
PARTITION BY PartitionNumber
ORDER BY (PartitionNumber, Id);
