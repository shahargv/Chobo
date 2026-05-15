SELECT id, shard, amount, note
FROM incremental_sharded_source.orders_local
ORDER BY id;
