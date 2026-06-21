@{
    Name = 'BackupRestoreSharded'
    Description = 'Exercises sharded backup and restore with one selected node per source shard.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'source-cluster' }
        @{ Name = 'restore'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'restore-cluster' }
        @{ Name = 'restore3'; Type = 'Cluster'; Shards = 3; Replicas = 1; DnsName = 'restore3-cluster' }
        @{ Name = 'single'; Type = 'SingleNode'; DnsName = 'single-node' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 180

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
        @{ Name = 'create-source-sharded-table'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-sharded-table.sql' }
        @{ Name = 'insert-source-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO backup_sharded_source.orders_local (id, shard, name) VALUES (101, 2, 's2-a'), (102, 2, 's2-b')" }
        @{ Name = 'create-single-source-and-append-targets'; Type = 'Sql'; Resource = 'single'; Path = 'Sql/create-single-source-and-append-targets.sql' }
        @{ Name = 'create-partial-compatible-target'; Type = 'Sql'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/create-partial-compatible-target.sql' }
        @{ Name = 'create-partial-failure-target'; Type = 'Sql'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/create-partial-failure-target.sql' }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'Cluster', '--node', 'clickhouse-source-s1-r1:9000', '--clickhouse-cluster-name', '{source.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(
                @{ Path = 'mode'; Equals = 'Cluster' }
                @{ Path = 'clickHouseClusterName'; Equals = '{source.ClusterName}' }
            )
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore', '--mode', 'Cluster', '--node', 'clickhouse-restore-s1-r1:9000', '--clickhouse-cluster-name', '{restore.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
        }
        @{
            Name = 'add-single-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'single', '--mode', 'SingleInstance', '--host', '{single.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'singleCluster'
        }
        @{
            Name = 'add-restore3-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore3', '--mode', 'Cluster', '--node', 'clickhouse-restore3-s1-r1:9000', '--clickhouse-cluster-name', '{restore3.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restore3Cluster'
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'start-manual-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreSharded/selector-sharded-orders.json')
            SaveJsonAs = 'backup'
        }
        @{
            Name = 'wait-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{backup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; Count = 2 }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'restore-preserve'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_sharded_source', '--table', 'orders_local', '--target-database', 'backup_sharded_restore', '--target-table', 'orders_local')
            SaveJsonAs = 'restore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Preserve' }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{
            Name = 'wait-restore-preserve'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'restore-source-shard-one-to-single'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{singleCluster.id}', '--database', 'backup_sharded_source', '--table', 'orders_local', '--target-database', 'backup_sharded_single', '--target-table', 'orders_shard_one', '--layout', 'single-node', '--source-shard', '1')
            SaveJsonAs = 'singleShardRestore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'SingleNode' }
                @{ Path = 'sourceShard'; Equals = 1 }
                @{ Path = 'tables[0].shards'; Count = 1 }
            )
        }
        @{
            Name = 'wait-source-shard-one-to-single'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{singleShardRestore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'restore-preserve-topology-mismatch-fails'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restore3Cluster.id}', '--database', 'backup_sharded_source', '--table', 'orders_local', '--target-database', 'backup_sharded_restore3_bad', '--target-table', 'orders_local')
            ExpectExitCode = 1
            ExpectTextContains = 'Preserve layout requires matching source and target shard counts'
        }
        @{
            Name = 'restore-partial-shard-failure'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_sharded_source', '--table', 'orders_local', '--target-database', 'backup_sharded_partial', '--target-table', 'orders_local', '--layout', 'preserve', '--append', '--confirm-destructive')
            SaveJsonAs = 'partialRestore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Preserve' }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{
            Name = 'wait-partial-shard-failure'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{partialRestore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'PartiallySucceeded' }
                @{ Path = 'failureReason'; NotEmpty = $true }
                @{ Path = 'tables[0].status'; Equals = 'PartiallySucceeded' }
                @{ Path = 'tables[0].error'; NotEmpty = $true }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2; status = 'Failed' } }
            )
        }
        @{
            Name = 'restore-redistribute-to-three-shards'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restore3Cluster.id}', '--database', 'backup_sharded_source', '--table', 'orders_local', '--target-database', 'backup_sharded_restore3', '--target-table', 'orders_local', '--layout', 'redistribute')
            SaveJsonAs = 'redistributeRestore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Redistribute' }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{
            Name = 'wait-redistribute-to-three-shards'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{redistributeRestore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; targetShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; targetShardNumber = 2; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'restore-append-single-shard'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{singleCluster.id}', '--database', 'backup_sharded_source', '--table', 'orders_local', '--target-database', 'backup_sharded_single', '--target-table', 'append_orders', '--layout', 'single-node', '--source-shard', '1', '--append', '--confirm-destructive')
            SaveJsonAs = 'appendRestore'
            ExpectJson = @(
                @{ Path = 'append'; Equals = $true }
                @{ Path = 'sourceShard'; Equals = 1 }
            )
        }
        @{
            Name = 'wait-append-single-shard'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{appendRestore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'backup-single-source'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{singleCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreSharded/selector-single-orders.json')
            SaveJsonAs = 'singleBackup'
        }
        @{
            Name = 'wait-single-source-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{singleBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; Count = 1 }
            )
        }
        @{
            Name = 'restore-single-backup-to-sharded'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{singleBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_single_source', '--table', 'orders_local', '--target-database', 'backup_single_to_sharded', '--target-table', 'orders_local', '--layout', 'redistribute')
            SaveJsonAs = 'singleToShardedRestore'
            ExpectJson = @(
                @{ Path = 'layout'; Equals = 'Redistribute' }
                @{ Path = 'tables[0].shards'; Count = 1 }
            )
        }
        @{
            Name = 'wait-single-backup-to-sharded'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{singleToShardedRestore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'show-audit-shards'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '500')
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'shards-prepared'; entityType = 'backup' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'shard-succeeded'; entityType = 'backup-table-shard' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'shard-failed'; entityType = 'restore-table-shard' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'table-partially-succeeded'; entityType = 'restore-table' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'partially-succeeded'; entityType = 'restore' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'shard-succeeded'; entityType = 'restore-table-shard' } }
            )
        }
    )

    Verify = @(
        @{ Name = 'verify-restore-shard-one'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-restore-shard-one.sql'; Expected = 'Expected/shard-one.csv'; Actual = 'restore-shard-one.csv' }
        @{ Name = 'verify-restore-shard-two'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-restore-shard-two.sql'; Expected = 'Expected/shard-two.csv'; Actual = 'restore-shard-two.csv' }
        @{ Name = 'verify-single-shard-restore'; Type = 'Csv'; Resource = 'single'; Path = 'Sql/select-single-shard-restore.sql'; Expected = 'Expected/shard-one.csv'; Actual = 'single-shard-restore.csv' }
        @{ Name = 'verify-partial-restore-succeeded-shard'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-partial-restore-shard-one.sql'; Expected = 'Expected/partial-shard-one-appended.csv'; Actual = 'partial-restore-shard-one.csv' }
        @{ Name = 'verify-partial-restore-failed-shard-left-original'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-partial-restore-shard-two-bad-table.sql'; Expected = 'Expected/bad-partial-target.csv'; Actual = 'partial-restore-shard-two-bad-table.csv' }
        @{ Name = 'verify-redistribute-shard-one'; Type = 'Csv'; Resource = 'restore3'; Host = 'clickhouse-restore3-s1-r1'; Path = 'Sql/select-redistribute-shard-one.sql'; Expected = 'Expected/shard-one.csv'; Actual = 'redistribute-shard-one.csv' }
        @{ Name = 'verify-redistribute-shard-two'; Type = 'Csv'; Resource = 'restore3'; Host = 'clickhouse-restore3-s2-r1'; Path = 'Sql/select-redistribute-shard-two.sql'; Expected = 'Expected/shard-two.csv'; Actual = 'redistribute-shard-two.csv' }
        @{ Name = 'verify-redistribute-shard-three-empty'; Type = 'Csv'; Resource = 'restore3'; Host = 'clickhouse-restore3-s3-r1'; Path = 'Sql/select-redistribute-shard-three.sql'; Expected = 'Expected/zero-count.csv'; Actual = 'redistribute-shard-three.csv' }
        @{ Name = 'verify-append-single-shard'; Type = 'Csv'; Resource = 'single'; Path = 'Sql/select-append-orders.sql'; Expected = 'Expected/append-orders.csv'; Actual = 'append-orders.csv' }
        @{ Name = 'verify-single-to-sharded'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s1-r1'; Path = 'Sql/select-single-to-sharded.sql'; Expected = 'Expected/single-source.csv'; Actual = 'single-to-sharded.csv' }
    )

    Cleanup = @()
}
