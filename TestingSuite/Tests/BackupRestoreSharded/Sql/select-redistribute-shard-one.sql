SELECT id, shard, name
FROM backup_sharded_restore3.orders_local
ORDER BY id
FORMAT CSV
