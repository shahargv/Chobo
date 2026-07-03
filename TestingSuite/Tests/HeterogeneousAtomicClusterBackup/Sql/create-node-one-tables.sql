DROP DATABASE IF EXISTS hetero_atomic_source SYNC;
CREATE DATABASE hetero_atomic_source ENGINE = Atomic;

CREATE TABLE hetero_atomic_source.table_a_node_one_only
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO hetero_atomic_source.table_a_node_one_only VALUES
    (101, 'node-1', 'a-only-on-access-node');

CREATE TABLE hetero_atomic_source.table_b_two_nodes_divergent
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO hetero_atomic_source.table_b_two_nodes_divergent VALUES
    (201, 'node-1', 'b-node-one-row');

CREATE TABLE hetero_atomic_source.table_d_all_shards_control
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO hetero_atomic_source.table_d_all_shards_control VALUES
    (401, 'node-1', 'd-shard-one-control');

