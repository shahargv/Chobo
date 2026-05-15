ALTER TABLE incremental_sharded_source.orders_local ON CLUSTER {source.ClusterName} ADD COLUMN amount UInt32 DEFAULT 0;
ALTER TABLE incremental_sharded_source.orders_local ON CLUSTER {source.ClusterName} ADD COLUMN note String DEFAULT '';
ALTER TABLE incremental_sharded_source.orders_local ON CLUSTER {source.ClusterName} DROP COLUMN name;

CREATE TABLE incremental_sharded_source.new_orders_local ON CLUSTER {source.ClusterName}
(
    id UInt64,
    shard UInt8,
    label String
)
ENGINE = MergeTree
ORDER BY id;
