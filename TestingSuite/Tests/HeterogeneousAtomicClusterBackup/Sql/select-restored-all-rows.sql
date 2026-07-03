SELECT table_name, id, origin, payload
FROM
(
    SELECT 'table_a_node_one_only' AS table_name, id, origin, payload
    FROM hetero_atomic_source.table_a_node_one_only
    UNION ALL
    SELECT 'table_b_two_nodes_divergent' AS table_name, id, origin, payload
    FROM hetero_atomic_source.table_b_two_nodes_divergent
    UNION ALL
    SELECT 'table_c_replicated' AS table_name, id, origin, payload
    FROM hetero_atomic_source.table_c_replicated
    UNION ALL
    SELECT 'table_d_all_shards_control' AS table_name, id, origin, payload
    FROM hetero_atomic_source.table_d_all_shards_control
    UNION ALL
    SELECT 'table_e_replica_only' AS table_name, id, origin, payload
    FROM hetero_atomic_source.table_e_replica_only
)
ORDER BY table_name, id
FORMAT CSV