CREATE DATABASE IF NOT EXISTS backup_cancel;
DROP TABLE IF EXISTS backup_cancel.orders;
CREATE TABLE backup_cancel.orders
(
    id UInt64,
    name String
)
ENGINE = MergeTree
ORDER BY id;
INSERT INTO backup_cancel.orders VALUES (1, 'alpha'), (2, 'beta');
