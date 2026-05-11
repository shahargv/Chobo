SELECT name, engine
FROM system.tables
WHERE database = 'backup_core_source'
  AND name IN ('join_lookup', 'log_events', 'merge_orders')
ORDER BY name
FORMAT CSV
