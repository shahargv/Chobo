@{
    Name = 'BackupRetentionCleanupSharded'
    Description = 'Verifies asynchronous backup cleanup removes every storage object for a sharded backup.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'source-cluster' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 180

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
        @{
            Name = 'mc-alias'
            Type = 'Shell'
            Args = @('mc', 'alias', 'set', 'chobo', 'http://minio:9000', '{backupStore.AccessKey}', '{backupStore.SecretKey}')
            ExpectTextContains = 'Added'
        }
        @{ Name = 'create-source-sharded-table'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-sharded-table.sql' }
        @{ Name = 'insert-source-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO backup_cleanup_sharded_source.orders_local (id, shard, name) VALUES (101, 2, 's2-a'), (102, 2, 's2-b')" }
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
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'start-sharded-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRetentionCleanupSharded/selector-sharded-orders.json')
            SaveJsonAs = 'backup'
        }
        @{
            Name = 'wait-sharded-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{backup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].shards'; Count = 2 }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; status = 'Succeeded' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'sharded-objects-exist-before-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 15
            RetryIntervalSeconds = 1
            ExpectTextContains = '{backup.id.n}'
        }
        @{
            Name = 'show-sharded-backup-detail'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{backup.id}')
            SaveJsonAs = 'completedBackup'
            ExpectJson = @(
                @{ Path = 'storageRootPath'; NotEmpty = $true }
                @{ Path = 'tables[0].shards'; Count = 2 }
            )
        }
        @{
            Name = 'write-unlisted-object-under-storage-root'
            Type = 'Shell'
            Args = @('sh', '-c', 'printf orphaned-attempt > /tmp/chobo-orphaned-attempt.txt && mc cp /tmp/chobo-orphaned-attempt.txt chobo/{backupStore.Bucket}/{completedBackup.storageRootPath}/unlisted/orphaned-attempt.txt')
            ExpectTextContains = 'orphaned-attempt.txt'
        }
        @{
            Name = 'unlisted-root-object-exists-before-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}/{completedBackup.storageRootPath}')
            RetryTimeoutSeconds = 15
            RetryIntervalSeconds = 1
            ExpectTextContains = 'unlisted/orphaned-attempt.txt'
        }
        @{
            Name = 'request-sharded-delete'
            Type = 'Cli'
            Args = @('backups', 'delete', '--id', '{backup.id}', '--confirm-destructive')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleteRequested' }
            )
        }
        @{
            Name = 'wait-sharded-deleted'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{backup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleted' }
                @{ Path = 'deletedAt'; NotEmpty = $true }
            )
        }
        @{
            Name = 'sharded-objects-gone-after-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 15
            RetryIntervalSeconds = 1
            ExpectTextNotContains = @('{backup.id.n}', '{completedBackup.storageRootPath}', 'unlisted/orphaned-attempt.txt')
        }
        @{
            Name = 'show-sharded-delete-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '200')
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'delete-requested'; entityType = 'backup'; entityId = '{backup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'backup-cleanup-succeeded'; entityType = 'backup'; entityId = '{backup.id}' } }
            )
        }
    )

    Verify = @()
    Cleanup = @()
}
