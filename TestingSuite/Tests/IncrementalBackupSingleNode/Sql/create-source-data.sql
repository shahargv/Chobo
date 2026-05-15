DROP DATABASE IF EXISTS incremental_single_source SYNC;
CREATE DATABASE incremental_single_source ENGINE = Atomic;

CREATE TABLE incremental_single_source.orders
(
    id UInt64,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO incremental_single_source.orders (id, name) VALUES (1, 'alpha'), (2, 'beta');
