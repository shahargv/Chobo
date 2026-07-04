@{
    Name = 'IncrementalBackupSharded'
    Description = 'Verifies cumulative incremental backup and restore on a sharded ClickHouse cluster, including per-shard parent paths and new-table full fallback.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'source-cluster' }
        @{ Name = 'restore'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'restore-cluster' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 240

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
        @{ Name = 'create-source-sharded-data'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-sharded-data.sql' }
        @{ Name = 'insert-source-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO incremental_sharded_source.orders_local (id, shard, name) VALUES (101, 2, 's2-a'), (102, 2, 's2-b')" }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'Cluster', '--node', 'clickhouse-source-s1-r1:9000', '--clickhouse-cluster-name', '{source.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore', '--mode', 'Cluster', '--node', 'clickhouse-restore-s1-r1:9000', '--clickhouse-cluster-name', '{restore.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'add-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'incremental-sharded', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/IncrementalBackupSharded/selector-all.json', '--full-retention-minutes', '10080', '--incremental-retention-minutes', '1440', '--min-backups-to-keep', '2', '--min-full-backups-to-keep', '1')
            SaveJsonAs = 'policy'
        }
        @{
            Name = 'start-full-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Full')
            SaveJsonAs = 'fullBackup'
        }
        @{
            Name = 'wait-full-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{fullBackup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].effectiveBackupType'; Equals = 'Full' }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{ Name = 'mutate-source-sharded-data'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/mutate-source-sharded-data.sql' }
        @{ Name = 'insert-mutated-source-shard-one'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s1-r1'; Query = "INSERT INTO incremental_sharded_source.orders_local (id, shard, amount, note) VALUES (3, 1, 30, 's1-new'); INSERT INTO incremental_sharded_source.new_orders_local (id, shard, label) VALUES (10, 1, 'new-s1')" }
        @{ Name = 'insert-mutated-source-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO incremental_sharded_source.orders_local (id, shard, amount, note) VALUES (103, 2, 40, 's2-new'); INSERT INTO incremental_sharded_source.new_orders_local (id, shard, label) VALUES (20, 2, 'new-s2')" }
        @{
            Name = 'start-incremental-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Incremental', '--refresh-cluster-metadata')
            SaveJsonAs = 'incrementalBackup'
        }
        @{
            Name = 'wait-incremental-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{incrementalBackup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'backupType'; Equals = 'Incremental' }
                @{ Path = 'tables'; ContainsObject = @{ table = 'orders_local'; effectiveBackupType = 'Incremental' } }
                @{ Path = 'tables'; ContainsObject = @{ table = 'new_orders_local'; effectiveBackupType = 'Full' } }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{
            Name = 'restore-incremental'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{incrementalBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--layout', 'preserve')
            SaveJsonAs = 'restore'
        }
        @{
            Name = 'wait-restore-incremental'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restore.id}', '--timeout-seconds', '90', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'entity-restore-plan-from-policy'
            Type = 'Cli'
            Args = @('restore', 'plan', '--policy-id', '{policy.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'incremental_sharded_source', '--table', 'orders_local', '--target-database', 'incremental_entity_restore', '--target-table', 'orders_local', '--layout', 'preserve')
            SaveJsonAs = 'entityPlan'
            ExpectJson = @(
                @{ Path = 'policyId'; Equals = '{policy.id}' }
                @{ Path = 'targetClusterId'; Equals = '{restoreCluster.id}' }
                @{ Path = 'tables[0].shards'; Count = 2 }
                @{ Path = 'queue'; Count = 2 }
                @{ Path = 'cliCommand'; Contains = 'restore initiate-from-plan' }
                @{ Path = 'cliJson'; Contains = 'incremental_entity_restore' }
            )
        }
        @{
            Name = 'entity-restore-from-policy'
            Type = 'Cli'
            Args = @('restore', 'initiate-from-policy', '--policy-id', '{policy.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'incremental_sharded_source', '--table', 'orders_local', '--target-database', 'incremental_entity_restore', '--target-table', 'orders_local', '--layout', 'preserve')
            SaveJsonAs = 'entityRestore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Preserve' }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{
            Name = 'wait-entity-restore-from-policy'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{entityRestore.id}', '--timeout-seconds', '90', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2; status = 'Succeeded' } }
            )
        }
    )

    Verify = @(
        @{ Name = 'verify-restored-orders-shard-one'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/orders-shard-one.csv'; Actual = 'orders-shard-one.csv' }
        @{ Name = 'verify-restored-orders-shard-two'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/orders-shard-two.csv'; Actual = 'orders-shard-two.csv' }
        @{ Name = 'verify-restored-new-orders-shard-one'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-restored-new-orders.sql'; Expected = 'Expected/new-orders-shard-one.csv'; Actual = 'new-orders-shard-one.csv' }
        @{ Name = 'verify-restored-new-orders-shard-two'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-restored-new-orders.sql'; Expected = 'Expected/new-orders-shard-two.csv'; Actual = 'new-orders-shard-two.csv' }
        @{ Name = 'verify-entity-policy-restore-shard-one'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-entity-policy-orders.sql'; Expected = 'Expected/orders-shard-one.csv'; Actual = 'entity-policy-orders-shard-one.csv' }
        @{ Name = 'verify-entity-policy-restore-shard-two'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-entity-policy-orders.sql'; Expected = 'Expected/orders-shard-two.csv'; Actual = 'entity-policy-orders-shard-two.csv' }
    )

    Cleanup = @()
}
