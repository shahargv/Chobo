SELECT id, shard, name
FROM backup_sharded_partial.orders_local
ORDER BY id
FORMAT CSV
