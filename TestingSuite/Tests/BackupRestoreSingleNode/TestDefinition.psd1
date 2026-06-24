@{
    Name = 'BackupRestoreSingleNode'
    Description = 'Backs up a MergeTree table from one single-node ClickHouse instance and restores it to another.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'SingleNode' }
        @{ Name = 'restore'; Type = 'SingleNode' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 90

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
        @{ Name = 'create-source-table'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-table.sql' }
        @{ Name = 'insert-source-rows'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/insert-source-rows.sql' }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'SingleInstance', '--host', '{source.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'source' }
                @{ Path = 'mode'; Equals = 'SingleInstance' }
            )
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore', '--mode', 'SingleInstance', '--host', '{restore.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'restore' }
            )
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'minio' }
            )
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'start-manual-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreSingleNode/selector-all.json')
            SaveJsonAs = 'backup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 'status'; Equals = 'Queued' }
            )
        }
        @{
            Name = 'wait-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{backup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'show-backup-single-shard-progress'
            Type = 'Cli'
            Args = @('backups', 'progress', '--id', '{backup.id}')
            ExpectTextContains = @(
                'backup_single_source.source_orders'
                'shard=Succeeded'
            )
            ExpectTextNotContains = 'shards=1 queued='
        }
        @{
            Name = 'start-restore'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_single_source', '--table', 'source_orders', '--target-database', 'backup_single_restore', '--target-table', 'restored_orders')
            SaveJsonAs = 'restoreRun'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 'status'; Equals = 'Queued' }
            )
        }
        @{
            Name = 'wait-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restoreRun.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
    )

    Verify = @(
        @{
            Name = 'verify-restored-rows'
            Type = 'Csv'
            Resource = 'restore'
            Path = 'Sql/select-restored-rows.sql'
            Expected = 'Expected/restored.csv'
            Actual = 'restored.csv'
        }
    )

    Cleanup = @()
}
