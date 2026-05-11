SELECT id, order_id, sku
FROM backup_core_source.line_items
ORDER BY id
FORMAT CSV
