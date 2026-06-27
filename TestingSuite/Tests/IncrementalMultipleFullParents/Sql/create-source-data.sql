DROP DATABASE IF EXISTS incremental_multi_parent_source SYNC;
CREATE DATABASE incremental_multi_parent_source ENGINE = Atomic;

CREATE TABLE incremental_multi_parent_source.orders
(
    id UInt32,
    name String
)
ENGINE = MergeTree
ORDER BY id;

CREATE TABLE incremental_multi_parent_source.invoices
(
    id UInt32,
    amount UInt32
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO incremental_multi_parent_source.orders (id, name) VALUES (1, 'order-full');
INSERT INTO incremental_multi_parent_source.invoices (id, amount) VALUES (1, 100);