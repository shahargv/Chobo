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
