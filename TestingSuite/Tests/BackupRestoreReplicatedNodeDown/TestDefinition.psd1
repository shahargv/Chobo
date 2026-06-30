@{
    Name = 'BackupRestoreReplicatedNodeDown'
    Description = 'Verifies backup and restore on a functioning two-shard, two-replica cluster with one replica down.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 2; Host = 'clickhouse-source-s1-r2'; DnsName = 'source-node-down-cluster'; DownNodes = @('s1-r1') }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 240
    UseDefaultCleanup = $false

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
        @{ Name = 'create-source-shard-one-live-replica'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s1-r2'; Path = 'Sql/create-source-replicated-data.sql' }
        @{ Name = 'create-source-shard-two-primary'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Path = 'Sql/create-source-replicated-data.sql' }
        @{ Name = 'create-source-shard-two-replica'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r2'; Path = 'Sql/create-source-replicated-data.sql' }
        @{ Name = 'insert-source-shard-one-live-replica'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s1-r2'; Query = "INSERT INTO backup_node_down_source.orders_local (id, shard, name) VALUES (1, 1, 's1-a'), (2, 1, 's1-b')" }
        @{ Name = 'insert-source-shard-two-primary'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO backup_node_down_source.orders_local (id, shard, name) VALUES (101, 2, 's2-a'), (102, 2, 's2-b')" }
        @{ Name = 'sync-source-shard-two-replica'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r2'; Query = 'SYSTEM SYNC REPLICA backup_node_down_source.orders_local' }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source-node-down', '--mode', 'Cluster', '--node', 'clickhouse-source-s1-r2:9000', '--clickhouse-cluster-name', '{source.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(
                @{ Path = 'mode'; Equals = 'Cluster' }
                @{ Path = 'clickHouseClusterName'; Equals = '{source.ClusterName}' }
            )
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'start-backup-with-source-node-down'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreReplicatedNodeDown/selector-replicated-orders.json')
            SaveJsonAs = 'backup'
        }
        @{
            Name = 'wait-backup-with-source-node-down'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{backup.id}', '--timeout-seconds', '90', '--poll-seconds', '1')
            SaveJsonAs = 'backupComplete'
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; Count = 2 }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; replicaNumber = 2; host = 'clickhouse-source-s1-r2'; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; status = 'Succeeded' } }
            )
        }
        @{ Name = 'drop-source-shard-one-live-replica-before-restore'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s1-r2'; Query = 'DROP DATABASE IF EXISTS backup_node_down_source SYNC' }
        @{ Name = 'drop-source-shard-two-primary-before-restore'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = 'DROP DATABASE IF EXISTS backup_node_down_source SYNC' }
        @{ Name = 'drop-source-shard-two-replica-before-restore'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r2'; Query = 'DROP DATABASE IF EXISTS backup_node_down_source SYNC' }
        @{
            Name = 'restore-preserve-with-one-replica-down'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{sourceCluster.id}', '--database', 'backup_node_down_source', '--table', 'orders_local', '--layout', 'preserve')
            SaveJsonAs = 'restore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Preserve' }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{
            Name = 'wait-restore-preserve-with-one-replica-down'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restore.id}', '--timeout-seconds', '90', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'show-audit-node-down-success'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '200')
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'shard-succeeded'; entityType = 'backup-table-shard' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'succeeded'; entityType = 'backup'; entityId = '{backup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'succeeded'; entityType = 'restore'; entityId = '{restore.id}' } }
            )
        }
    )

    Verify = @(
        @{ Name = 'verify-restored-shard-one-live-replica'; Type = 'Csv'; Resource = 'source'; Host = 'clickhouse-source-s1-r2'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/shard-one.csv'; Actual = 'restored-shard-one.csv' }
        @{ Name = 'verify-restored-shard-two-primary'; Type = 'Csv'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/shard-two.csv'; Actual = 'restored-shard-two.csv' }
    )

    Cleanup = @()
}


