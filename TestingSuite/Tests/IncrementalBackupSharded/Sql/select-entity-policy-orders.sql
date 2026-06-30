SELECT id, shard, amount, note
FROM incremental_entity_restore.orders_local
ORDER BY id;
