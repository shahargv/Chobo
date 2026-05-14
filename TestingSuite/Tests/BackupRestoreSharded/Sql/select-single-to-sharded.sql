SELECT id, shard, name
FROM backup_single_to_sharded.orders_local
ORDER BY id
FORMAT CSV
