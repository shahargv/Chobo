SELECT id, shard, name
FROM backup_sharded_single.orders_shard_one
ORDER BY id
FORMAT CSV
