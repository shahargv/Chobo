SELECT id, shard, label
FROM incremental_sharded_source.new_orders_local
ORDER BY id;
