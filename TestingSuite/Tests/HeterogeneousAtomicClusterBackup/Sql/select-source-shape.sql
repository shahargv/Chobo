SELECT table_name, arraySort(groupArray(host)) AS hosts
FROM
(
    SELECT 'clickhouse-source-s1-r1' AS host, name AS table_name FROM remote('clickhouse-source-s1-r1', 'system', 'tables') WHERE database = 'hetero_atomic_source' AND startsWith(name, 'table_')
    UNION ALL
    SELECT 'clickhouse-source-s2-r1' AS host, name AS table_name FROM remote('clickhouse-source-s2-r1', 'system', 'tables') WHERE database = 'hetero_atomic_source' AND startsWith(name, 'table_')
    UNION ALL
    SELECT 'clickhouse-source-s2-r2' AS host, name AS table_name FROM remote('clickhouse-source-s2-r2', 'system', 'tables') WHERE database = 'hetero_atomic_source' AND startsWith(name, 'table_')
    UNION ALL
    SELECT 'clickhouse-source-s3-r1' AS host, name AS table_name FROM remote('clickhouse-source-s3-r1', 'system', 'tables') WHERE database = 'hetero_atomic_source' AND startsWith(name, 'table_')
    UNION ALL
    SELECT 'clickhouse-source-s3-r2' AS host, name AS table_name FROM remote('clickhouse-source-s3-r2', 'system', 'tables') WHERE database = 'hetero_atomic_source' AND startsWith(name, 'table_')
)
GROUP BY table_name
ORDER BY table_name
FORMAT CSV