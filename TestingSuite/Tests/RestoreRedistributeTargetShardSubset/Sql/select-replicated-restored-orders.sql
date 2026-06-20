SELECT id, shard, name
FROM redistribute_subset_replicated_restore.orders_local
ORDER BY id
FORMAT CSV
