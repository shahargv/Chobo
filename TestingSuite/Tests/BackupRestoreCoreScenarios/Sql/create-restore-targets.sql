DROP DATABASE IF EXISTS backup_core_restore SYNC;
CREATE DATABASE IF NOT EXISTS backup_core_restore ENGINE = Atomic;

CREATE TABLE backup_core_restore.append_orders
(
    id UInt32,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_core_restore.append_orders (id, name) VALUES
(100, 'existing');

CREATE TABLE backup_core_restore.compatible_existing_orders
(
    id UInt32,
    name String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_core_restore.compatible_existing_orders (id, name) VALUES
(300, 'compatible-existing');

CREATE TABLE backup_core_restore.bad_orders
(
    id UInt32,
    amount UInt32
)
ENGINE = MergeTree
ORDER BY id;

CREATE TABLE backup_core_restore.mismatch_allowed_orders
(
    id UInt32,
    name String,
    note String DEFAULT 'from-default'
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO backup_core_restore.mismatch_allowed_orders (id, name, note) VALUES
(200, 'already-there', 'existing-note');
