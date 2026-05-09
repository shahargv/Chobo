DROP DATABASE IF EXISTS backup_core_source SYNC;
CREATE DATABASE IF NOT EXISTS backup_core_source ENGINE = Atomic;

CREATE TABLE backup_core_source.orders
(
    id UInt32,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_core_source.orders (id, name) VALUES
(1, 'alpha'),
(2, 'beta');

CREATE TABLE backup_core_source.line_items
(
    id UInt32,
    order_id UInt32,
    sku String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_core_source.line_items (id, order_id, sku) VALUES
(10, 1, 'sku-a'),
(11, 2, 'sku-b');

CREATE TABLE backup_core_source.log_events
(
    id UInt32,
    message String
)
ENGINE = Log;

INSERT INTO backup_core_source.log_events (id, message) VALUES
(100, 'schema-only-source-row');

CREATE TABLE backup_core_source.join_lookup
(
    id UInt32,
    name String
)
ENGINE = Join(ANY, LEFT, id);

INSERT INTO backup_core_source.join_lookup (id, name) VALUES
(1, 'join-only-source-row');

CREATE TABLE backup_core_source.merge_orders
(
    id UInt32,
    name String
)
ENGINE = Merge(backup_core_source, '^orders$');
