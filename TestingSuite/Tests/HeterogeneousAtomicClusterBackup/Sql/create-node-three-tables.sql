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

CREATE TABLE hetero_atomic_source.table_e_replica_only
(
    id UInt64,
    origin String,
    payload String
)
ENGINE = MergeTree
ORDER BY id;

INSERT INTO hetero_atomic_source.table_e_replica_only VALUES
    (501, 'node-3', 'e-only-on-non-representative-replica');
