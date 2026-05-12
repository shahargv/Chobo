SELECT id, shard, name
FROM backup_sharded_restore.orders_local
ORDER BY id
FORMAT CSV
