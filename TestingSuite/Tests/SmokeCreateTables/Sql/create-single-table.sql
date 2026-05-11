DROP DATABASE IF EXISTS smoke_single_db SYNC;
CREATE DATABASE IF NOT EXISTS smoke_single_db ENGINE = Atomic;

CREATE TABLE IF NOT EXISTS smoke_single_db.smoke_single_orders
(
    Id UInt32,
    Name String,
    Amount UInt32
)
ENGINE = MergeTree
ORDER BY Id;
