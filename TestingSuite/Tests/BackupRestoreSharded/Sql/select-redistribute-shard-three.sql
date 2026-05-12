SELECT count()
FROM system.tables
WHERE database = 'backup_sharded_restore3'
  AND name = 'orders_local'
FORMAT CSV
