@{
    Name = 'RestoreRedistributeTargetShardSubset'
    Description = 'Restores data from three source shards into a selected two-shard target pool while leaving the table declared on every target shard for Atomic and Replicated database engines.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 3; Replicas = 1; DnsName = 'subset-source-cluster' }
        @{ Name = 'restore'; Type = 'Cluster'; Shards = 3; Replicas = 1; DnsName = 'subset-restore-cluster' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 240

    Setup = @(
        @{
            Name = 'wait-server-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 60
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
        @{ Name = 'create-source-tables'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-table.sql' }
        @{ Name = 'create-replicated-restore-database'; Type = 'Sql'; Resource = 'restore'; Path = 'Sql/create-replicated-restore-database.sql' }
        @{ Name = 'insert-atomic-source-shard-one'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s1-r1'; Query = "INSERT INTO redistribute_subset_source.orders_local (id, shard, name) VALUES (1, 1, 's1-a'), (2, 1, 's1-b')" }
        @{ Name = 'insert-atomic-source-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO redistribute_subset_source.orders_local (id, shard, name) VALUES (101, 2, 's2-a'), (102, 2, 's2-b')" }
        @{ Name = 'insert-atomic-source-shard-three'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s3-r1'; Query = "INSERT INTO redistribute_subset_source.orders_local (id, shard, name) VALUES (301, 3, 's3-a'), (302, 3, 's3-b')" }
        @{ Name = 'insert-replicated-source-shard-one'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s1-r1'; Query = "INSERT INTO redistribute_subset_replicated_source.orders_local (id, shard, name) VALUES (1, 1, 's1-a'), (2, 1, 's1-b')" }
        @{ Name = 'insert-replicated-source-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO redistribute_subset_replicated_source.orders_local (id, shard, name) VALUES (101, 2, 's2-a'), (102, 2, 's2-b')" }
        @{ Name = 'insert-replicated-source-shard-three'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s3-r1'; Query = "INSERT INTO redistribute_subset_replicated_source.orders_local (id, shard, name) VALUES (301, 3, 's3-a'), (302, 3, 's3-b')" }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'subset-source', '--mode', 'Cluster', '--node', 'clickhouse-source-s1-r1:9000', '--clickhouse-cluster-name', '{source.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(
                @{ Path = 'mode'; Equals = 'Cluster' }
                @{ Path = 'clickHouseClusterName'; Equals = '{source.ClusterName}' }
            )
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'subset-restore', '--mode', 'Cluster', '--node', 'clickhouse-restore-s1-r1:9000', '--clickhouse-cluster-name', '{restore.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'start-atomic-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/RestoreRedistributeTargetShardSubset/selector-orders.json')
            SaveJsonAs = 'atomicBackup'
        }
        @{
            Name = 'wait-atomic-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{atomicBackup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; Count = 3 }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 3; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'restore-atomic-to-selected-target-shards'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{atomicBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'redistribute_subset_source', '--table', 'orders_local', '--target-database', 'redistribute_subset_restore', '--target-table', 'orders_local', '--layout', 'redistribute', '--target-shards', '1,2')
            SaveJsonAs = 'atomicRestore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Redistribute' }
                @{ Path = 'tables[0].shards'; Count = 3 }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1 } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2 } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 3; targetShardNumber = 1 } }
            )
        }
        @{
            Name = 'wait-atomic-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{atomicRestore.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 3; targetShardNumber = 1; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'start-replicated-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/RestoreRedistributeTargetShardSubset/selector-replicated-orders.json')
            SaveJsonAs = 'replicatedBackup'
        }
        @{
            Name = 'wait-replicated-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{replicatedBackup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; Count = 3 }
            )
        }
        @{
            Name = 'restore-replicated-to-selected-target-shards'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{replicatedBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'redistribute_subset_replicated_source', '--table', 'orders_local', '--target-database', 'redistribute_subset_replicated_restore', '--target-table', 'orders_local', '--layout', 'redistribute', '--target-shards', '1,2')
            SaveJsonAs = 'replicatedRestore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Redistribute' }
                @{ Path = 'tables[0].shards'; Count = 3 }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1 } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2 } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 3; targetShardNumber = 1 } }
            )
        }
        @{
            Name = 'wait-replicated-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{replicatedRestore.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 3; targetShardNumber = 1; status = 'Succeeded' } }
            )
        }
    )

    Verify = @(
        @{ Name = 'verify-atomic-target-shard-one-has-source-shards-one-and-three'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/target-shard-one.csv'; Actual = 'atomic-target-shard-one.csv' }
        @{ Name = 'verify-atomic-target-shard-two-has-source-shard-two'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/target-shard-two.csv'; Actual = 'atomic-target-shard-two.csv' }
        @{ Name = 'verify-atomic-target-shard-three-has-table'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s3-r1'; Path = 'Sql/select-unused-target-table-count.sql'; Expected = 'Expected/one-count.csv'; Actual = 'atomic-target-shard-three-table-count.csv' }
        @{ Name = 'verify-atomic-target-shard-three-has-no-data'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s3-r1'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/empty.csv'; Actual = 'atomic-target-shard-three-data.csv' }
        @{ Name = 'verify-replicated-target-shard-one-has-source-shards-one-and-three'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-replicated-restored-orders.sql'; Expected = 'Expected/target-shard-one.csv'; Actual = 'replicated-target-shard-one.csv' }
        @{ Name = 'verify-replicated-target-shard-two-has-source-shard-two'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-replicated-restored-orders.sql'; Expected = 'Expected/target-shard-two.csv'; Actual = 'replicated-target-shard-two.csv' }
        @{ Name = 'verify-replicated-target-shard-three-has-table'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s3-r1'; Path = 'Sql/select-replicated-target-table-count.sql'; Expected = 'Expected/one-count.csv'; Actual = 'replicated-target-shard-three-table-count.csv' }
        @{ Name = 'verify-replicated-target-shard-three-has-no-data'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s3-r1'; Path = 'Sql/select-replicated-restored-orders.sql'; Expected = 'Expected/empty.csv'; Actual = 'replicated-target-shard-three-data.csv' }
    )

    Cleanup = @()
}

