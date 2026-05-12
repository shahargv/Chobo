DROP DATABASE IF EXISTS backup_failure_single SYNC;
CREATE DATABASE backup_failure_single;

CREATE TABLE backup_failure_single.orders
(
    id UInt64,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_failure_single.orders (id, name) VALUES (1, 'single-a'), (2, 'single-b');
