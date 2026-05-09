CREATE TABLE IF NOT EXISTS {single.DatabaseName}.{single.TableName}
(
    Id UInt32,
    Name String,
    Amount UInt32
)
ENGINE = MergeTree
ORDER BY Id;
