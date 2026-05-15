@{
    Name = 'BackupMetadataRecovery'
    Description = 'Rebuilds backup metadata from S3 manifests after SQLite loss, including single-node, sharded, incremental, and failed backup records.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'sourceSingle'; Type = 'SingleNode' }
        @{ Name = 'restoreSingle'; Type = 'SingleNode' }
        @{ Name = 'sourceSharded'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'source-sharded' }
        @{ Name = 'restoreSharded'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'restore-sharded' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 360

    Setup = @(
        @{
            Name = 'wait-server-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectJson = @(@{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } })
        }
        @{
            Name = 'auth-profile'
            Type = 'Cli'
            Args = @('server', 'auth', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            ExpectTextContains = 'Authenticated to http://choboserver:8080.'
        }
        @{ Name = 'create-single-source'; Type = 'Sql'; Resource = 'sourceSingle'; Path = 'Sql/create-single-source.sql' }
        @{ Name = 'create-sharded-source'; Type = 'Sql'; Resource = 'sourceSharded'; Path = 'Sql/create-sharded-source.sql' }
        @{ Name = 'insert-sharded-shard-two'; Type = 'Sql'; Resource = 'sourceSharded'; Host = 'clickhouse-sourcesharded-s2-r1'; Path = 'Sql/insert-sharded-shard-two.sql' }
    )

    Action = @(
        @{
            Name = 'add-single-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source-single', '--mode', 'SingleInstance', '--host', '{sourceSingle.Host}', '--username', 'default')
            SaveJsonAs = 'sourceSingleCluster'
        }
        @{
            Name = 'add-sharded-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source-sharded', '--mode', 'Cluster', '--node', 'clickhouse-sourcesharded-s1-r1:9000', '--clickhouse-cluster-name', '{sourceSharded.ClusterName}', '--backup-restore-maxdop', '1', '--username', 'default')
            SaveJsonAs = 'sourceShardedCluster'
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'add-single-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'single-recovery', '--source-cluster-id', '{sourceSingleCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupMetadataRecovery/selector-all.json')
            SaveJsonAs = 'singlePolicy'
        }
        @{
            Name = 'add-sharded-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'sharded-recovery', '--source-cluster-id', '{sourceShardedCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupMetadataRecovery/selector-all.json')
            SaveJsonAs = 'shardedPolicy'
        }
        @{ Name = 'start-single-full'; Type = 'Cli'; Args = @('backup', 'manual', '--policy-id', '{singlePolicy.id}', '--backup-type', 'Full'); SaveJsonAs = 'singleFull' }
        @{ Name = 'wait-single-full'; Type = 'Cli'; Args = @('backups', 'wait', '--id', '{singleFull.id}', '--timeout-seconds', '60', '--poll-seconds', '1'); ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' }) }
        @{ Name = 'mutate-single-source'; Type = 'Sql'; Resource = 'sourceSingle'; Path = 'Sql/mutate-single-source.sql' }
        @{ Name = 'start-single-incremental'; Type = 'Cli'; Args = @('backup', 'manual', '--policy-id', '{singlePolicy.id}', '--backup-type', 'Incremental'); SaveJsonAs = 'singleIncremental' }
        @{ Name = 'wait-single-incremental'; Type = 'Cli'; Args = @('backups', 'wait', '--id', '{singleIncremental.id}', '--timeout-seconds', '60', '--poll-seconds', '1'); ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' }) }
        @{ Name = 'start-sharded-full'; Type = 'Cli'; Args = @('backup', 'manual', '--policy-id', '{shardedPolicy.id}', '--backup-type', 'Full'); SaveJsonAs = 'shardedFull' }
        @{ Name = 'wait-sharded-full'; Type = 'Cli'; Args = @('backups', 'wait', '--id', '{shardedFull.id}', '--timeout-seconds', '90', '--poll-seconds', '1'); ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' }) }
        @{ Name = 'mutate-sharded-source-one'; Type = 'Sql'; Resource = 'sourceSharded'; Path = 'Sql/mutate-sharded-source.sql' }
        @{ Name = 'mutate-sharded-source-two'; Type = 'Sql'; Resource = 'sourceSharded'; Host = 'clickhouse-sourcesharded-s2-r1'; Path = 'Sql/mutate-sharded-shard-two.sql' }
        @{ Name = 'start-sharded-incremental'; Type = 'Cli'; Args = @('backup', 'manual', '--policy-id', '{shardedPolicy.id}', '--backup-type', 'Incremental'); SaveJsonAs = 'shardedIncremental' }
        @{ Name = 'wait-sharded-incremental'; Type = 'Cli'; Args = @('backups', 'wait', '--id', '{shardedIncremental.id}', '--timeout-seconds', '90', '--poll-seconds', '1'); ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' }) }
        @{
            Name = 'seed-failed-backup'
            Type = 'Cli'
            Args = @('test-hooks', 'seed-missing-backup-operation', '--source-cluster-id', '{sourceSingleCluster.id}', '--target-id', '{target.id}', '--database', 'metadata_recovery_single', '--table', 'orders')
            SaveJsonAs = 'failedBackup'
        }
        @{
            Name = 'wait-failed-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{failedBackup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(@{ Path = 'status'; Equals = 'Failed' })
        }
        @{
            Name = 'delete-sqlite'
            Type = 'Cli'
            Args = @('test-hooks', 'delete-sqlite-and-crash')
            ExpectJson = @(@{ Path = 'deletingSqlite'; Equals = $true })
        }
        @{
            Name = 'wait-fresh-server'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectJson = @(@{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } })
        }
        @{
            Name = 'auth-fresh-profile'
            Type = 'Cli'
            Args = @('server', 'auth', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            ExpectTextContains = 'Authenticated to http://choboserver:8080.'
        }
        @{
            Name = 'add-recovery-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'recovery-minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'recoveryTarget'
        }
        @{
            Name = 'recover-backup-metadata'
            Type = 'Cli'
            Args = @('backups', 'recover', '--target-id', '{recoveryTarget.id}', '--scan-root', 'backups')
            ExpectJson = @(
                @{ Path = 'importedBackupCount'; Equals = '5' }
            )
        }
        @{ Name = 'update-single-credentials'; Type = 'Cli'; Args = @('clusters', 'update-credentials', '--id', '{sourceSingleCluster.id}', '--username', 'default') }
        @{ Name = 'update-sharded-credentials'; Type = 'Cli'; Args = @('clusters', 'update-credentials', '--id', '{sourceShardedCluster.id}', '--username', 'default') }
        @{ Name = 'add-restore-single'; Type = 'Cli'; Args = @('clusters', 'add', '--name', 'restore-single', '--mode', 'SingleInstance', '--host', '{restoreSingle.Host}', '--username', 'default'); SaveJsonAs = 'restoreSingleCluster' }
        @{ Name = 'add-restore-sharded'; Type = 'Cli'; Args = @('clusters', 'add', '--name', 'restore-sharded', '--mode', 'Cluster', '--node', 'clickhouse-restoresharded-s1-r1:9000', '--clickhouse-cluster-name', '{restoreSharded.ClusterName}', '--backup-restore-maxdop', '1', '--username', 'default'); SaveJsonAs = 'restoreShardedCluster' }
        @{ Name = 'restore-single-incremental'; Type = 'Cli'; Args = @('restore', 'initiate', '--backup-id', '{singleIncremental.id}', '--target-cluster-id', '{restoreSingleCluster.id}'); SaveJsonAs = 'singleRestore' }
        @{ Name = 'wait-restore-single'; Type = 'Cli'; Args = @('restores', 'wait', '--id', '{singleRestore.id}', '--timeout-seconds', '90', '--poll-seconds', '1'); ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' }) }
        @{ Name = 'restore-sharded-incremental'; Type = 'Cli'; Args = @('restore', 'initiate', '--backup-id', '{shardedIncremental.id}', '--target-cluster-id', '{restoreShardedCluster.id}', '--layout', 'preserve'); SaveJsonAs = 'shardedRestore' }
        @{ Name = 'wait-restore-sharded'; Type = 'Cli'; Args = @('restores', 'wait', '--id', '{shardedRestore.id}', '--timeout-seconds', '120', '--poll-seconds', '1'); ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' }) }
        @{ Name = 'show-recovered-failed'; Type = 'Cli'; Args = @('backups', 'show', '--id', '{failedBackup.id}'); ExpectJson = @(@{ Path = 'status'; Equals = 'Failed' }) }
    )

    Verify = @(
        @{ Name = 'verify-single-restored'; Type = 'Csv'; Resource = 'restoreSingle'; Path = 'Sql/select-single-restored.sql'; Expected = 'Expected/single-restored.csv'; Actual = 'single-restored.csv' }
        @{ Name = 'verify-sharded-restored-one'; Type = 'Csv'; Resource = 'restoreSharded'; Host = 'clickhouse-restoresharded-s1-r1'; Path = 'Sql/select-sharded-restored.sql'; Expected = 'Expected/sharded-restored-shard-one.csv'; Actual = 'sharded-restored-one.csv' }
        @{ Name = 'verify-sharded-restored-two'; Type = 'Csv'; Resource = 'restoreSharded'; Host = 'clickhouse-restoresharded-s2-r1'; Path = 'Sql/select-sharded-restored.sql'; Expected = 'Expected/sharded-restored-shard-two.csv'; Actual = 'sharded-restored-two.csv' }
    )

    Cleanup = @()
}
