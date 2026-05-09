DROP DATABASE IF EXISTS failing_basic_db SYNC;
CREATE DATABASE IF NOT EXISTS failing_basic_db ENGINE = Atomic;

CREATE TABLE IF NOT EXISTS failing_basic_db.failing_basic_orders
(
    Id UInt32,
    Name String,
    Amount UInt32
)
ENGINE = MergeTree
ORDER BY Id;
