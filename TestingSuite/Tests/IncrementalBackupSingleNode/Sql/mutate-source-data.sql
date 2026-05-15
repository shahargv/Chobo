ALTER TABLE incremental_single_source.orders ADD COLUMN amount UInt32 DEFAULT 0;
ALTER TABLE incremental_single_source.orders ADD COLUMN note String DEFAULT '';
INSERT INTO incremental_single_source.orders (id, name, amount, note) VALUES (3, 'gamma', 30, 'new-row');
ALTER TABLE incremental_single_source.orders DROP COLUMN name;

CREATE TABLE incremental_single_source.new_orders
(
    id UInt64,
    label String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO incremental_single_source.new_orders (id, label) VALUES (10, 'created-after-full');
