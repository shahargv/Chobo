DROP DATABASE IF EXISTS hetero_atomic_source SYNC;
CREATE DATABASE hetero_atomic_source ENGINE = Atomic;

CREATE TABLE hetero_atomic_source.table_b_two_nodes_divergent
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO hetero_atomic_source.table_b_two_nodes_divergent VALUES
    (202, 'node-2', 'b-node-two-different-row');

CREATE TABLE hetero_atomic_source.table_c_replicated
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = ReplicatedMergeTree('/clickhouse/tables/hetero_atomic_source/table_c_replicated/{shard}', '{replica}')
ORDER BY id;

INSERT INTO hetero_atomic_source.table_c_replicated VALUES
    (301, 'node-2-node-3-replicated-shard', 'c-shard-two-row');

CREATE TABLE hetero_atomic_source.table_d_all_shards_control
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO hetero_atomic_source.table_d_all_shards_control VALUES
    (402, 'node-2', 'd-shard-two-control');

