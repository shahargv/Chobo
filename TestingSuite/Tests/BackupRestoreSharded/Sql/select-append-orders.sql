SELECT id, shard, name
FROM backup_sharded_single.append_orders
ORDER BY id
FORMAT CSV
