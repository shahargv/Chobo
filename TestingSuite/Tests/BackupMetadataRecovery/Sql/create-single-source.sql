CREATE DATABASE IF NOT EXISTS metadata_recovery_single ENGINE = Atomic;
CREATE TABLE IF NOT EXISTS metadata_recovery_single.orders
(
    id UInt64,
    label String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO metadata_recovery_single.orders VALUES (1, 'single-full');
