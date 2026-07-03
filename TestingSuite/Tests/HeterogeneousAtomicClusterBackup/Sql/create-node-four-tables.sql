DROP DATABASE IF EXISTS hetero_atomic_source SYNC;
CREATE DATABASE hetero_atomic_source ENGINE = Atomic;

CREATE TABLE hetero_atomic_source.table_c_replicated
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = ReplicatedMergeTree('/clickhouse/tables/hetero_atomic_source/table_c_replicated/{shard}', '{replica}')
ORDER BY id;

INSERT INTO hetero_atomic_source.table_c_replicated VALUES
    (302, 'node-4-node-5-replicated-shard', 'c-shard-three-row');

CREATE TABLE hetero_atomic_source.table_d_all_shards_control
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO hetero_atomic_source.table_d_all_shards_control VALUES
    (403, 'node-4', 'd-shard-three-control');

