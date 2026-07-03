@{
    Name = 'HeterogeneousAtomicClusterBackup'
    Description = 'Backs up and restores an intentionally uneven Atomic cluster: five populated nodes plus an empty replica, node-local tables, non-replicated divergent shard data, replicated tables missing from the access node, non-representative replica data, and a normal control table.'

    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 3; Replicas = 2; DnsName = 'hetero-source-cluster' }
        @{ Name = 'restore'; Type = 'SingleNode'; DnsName = 'hetero-restore-node' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'hetero-backup-s3' }
    )
    TimeoutSeconds = 300
    UseDefaultReplicaSync = $false

    Setup = @(
        @{
            Name = 'wait-server-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } }
            )
        }
        @{
            Name = 'auth-profile'
            Type = 'Cli'
            Args = @('server', 'auth', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            ExpectTextContains = 'Authenticated to http://choboserver:8080.'
        }

        @{ Name = 'create-node-one-tables'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s1-r1'; Path = 'Sql/create-node-one-tables.sql' }
        @{ Name = 'create-node-two-tables'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Path = 'Sql/create-node-two-tables.sql' }
        @{ Name = 'create-node-three-tables'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r2'; Path = 'Sql/create-node-three-tables.sql' }
        @{ Name = 'create-node-four-tables'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s3-r1'; Path = 'Sql/create-node-four-tables.sql' }
        @{ Name = 'create-node-five-tables'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s3-r2'; Path = 'Sql/create-node-five-tables.sql' }
        @{ Name = 'sync-replicated-table-c-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r2'; Query = 'SYSTEM SYNC REPLICA hetero_atomic_source.table_c_replicated' }
        @{ Name = 'sync-replicated-table-c-shard-three'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s3-r2'; Query = 'SYSTEM SYNC REPLICA hetero_atomic_source.table_c_replicated' }

        @{
            Name = 'assert-source-shape'
            Type = 'Csv'
            Resource = 'source'
            Host = 'clickhouse-source-s1-r1'
            Path = 'Sql/select-source-shape.sql'
            Expected = 'Expected/source-shape.csv'
            Actual = 'source-shape.csv'
        }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'heterogeneous-source', '--mode', 'Cluster', '--node', 'clickhouse-source-s1-r1:9000', '--clickhouse-cluster-name', '{source.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(
                @{ Path = 'mode'; Equals = 'Cluster' }
                @{ Path = 'clickHouseClusterName'; Equals = '{source.ClusterName}' }
            )
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'heterogeneous-restore', '--mode', 'SingleInstance', '--host', '{restore.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'start-heterogeneous-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/HeterogeneousAtomicClusterBackup/selector-heterogeneous-restorable.json')
            SaveJsonAs = 'backup'
        }
        @{
            Name = 'wait-heterogeneous-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{backup.id}', '--timeout-seconds', '90', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 5 }
                @{ Path = 'tables'; ContainsObject = @{ database = 'hetero_atomic_source'; table = 'table_a_node_one_only'; status = 'Succeeded' } }
                @{ Path = 'tables'; ContainsObject = @{ database = 'hetero_atomic_source'; table = 'table_b_two_nodes_divergent'; status = 'Succeeded' } }
                @{ Path = 'tables'; ContainsObject = @{ database = 'hetero_atomic_source'; table = 'table_c_replicated'; status = 'Succeeded' } }
                @{ Path = 'tables'; ContainsObject = @{ database = 'hetero_atomic_source'; table = 'table_d_all_shards_control'; status = 'Succeeded' } }
                @{ Path = 'tables'; ContainsObject = @{ database = 'hetero_atomic_source'; table = 'table_e_replica_only'; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'restore-all-to-single'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--layout', 'single-node')
            SaveJsonAs = 'restore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'SingleNode' }
                @{ Path = 'tables'; Count = 5 }
            )
        }
        @{
            Name = 'wait-restore-all-to-single'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restore.id}', '--timeout-seconds', '120', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
    )

    Verify = @(
        @{ Name = 'verify-restored-all-rows'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-restored-all-rows.sql'; Expected = 'Expected/all-restored-rows.csv'; Actual = 'all-restored-rows.csv' }
    )

    Cleanup = @()
}


