CREATE TABLE IF NOT EXISTS {cluster2.DatabaseName}.{cluster2.TableName}
(
    Id UInt32,
    EventDate Date,
    Name String,
    Amount Decimal(10, 2)
)
ENGINE = ReplicatedMergeTree
ORDER BY (EventDate, Id);
