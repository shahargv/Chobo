SELECT count()
FROM system.tables
WHERE database = 'redistribute_subset_replicated_restore' AND name = 'orders_local'
FORMAT CSV
