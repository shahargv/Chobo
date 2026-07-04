@{
    Name = 'IncrementalBackupSingleNode'
    Description = 'Verifies cumulative incremental backup and restore on a single ClickHouse node, including schema changes and a new table fallback to full.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'SingleNode' }
        @{ Name = 'restore'; Type = 'SingleNode' }
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
        @{ Name = 'create-source-data'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-data.sql' }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'SingleInstance', '--host', '{source.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore', '--mode', 'SingleInstance', '--host', '{restore.Host}', '--backup-restore-maxdop', '1')
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
            Args = @('policies', 'add', '--name', 'incremental-single', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/IncrementalBackupSingleNode/selector-all.json', '--full-retention-minutes', '10080', '--incremental-retention-minutes', '1440', '--min-backups-to-keep', '2', '--min-full-backups-to-keep', '1')
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
            Args = @('backups', 'wait', '--id', '{fullBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'startedAt'; NotEmpty = $true }
                @{ Path = 'endedAt'; NotEmpty = $true }
                @{ Path = 'backupType'; Equals = 'Full' }
                @{ Path = 'tables[0].effectiveBackupType'; Equals = 'Full' }
                @{ Path = 'storageRootPath'; Contains = 'backups/runs/policy-{policy.id.n}/' }
                @{ Path = 'tables[0].storagePath'; Contains = '/tables/incremental_single_source/orders/full' }
            )
        }
        @{ Name = 'mutate-source-data'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/mutate-source-data.sql' }
        @{
            Name = 'start-incremental-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Incremental', '--refresh-cluster-metadata')
            SaveJsonAs = 'incrementalBackup'
        }
        @{
            Name = 'wait-incremental-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{incrementalBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'startedAt'; NotEmpty = $true }
                @{ Path = 'endedAt'; NotEmpty = $true }
                @{ Path = 'backupType'; Equals = 'Incremental' }
                @{ Path = 'tables'; ContainsObject = @{ table = 'orders'; effectiveBackupType = 'Incremental' } }
                @{ Path = 'tables'; ContainsObject = @{ table = 'new_orders'; effectiveBackupType = 'Full' } }
                @{ Path = 'tables[1].storagePath'; Contains = 'parent-full-{fullBackup.id.n}' }
            )
        }
        @{
            Name = 'restore-incremental'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{incrementalBackup.id}', '--target-cluster-id', '{restoreCluster.id}')
            SaveJsonAs = 'restore'
        }
        @{
            Name = 'wait-restore-incremental'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restore.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'startedAt'; NotEmpty = $true }
                @{ Path = 'endedAt'; NotEmpty = $true }
            )
        }
    )

    Verify = @(
        @{ Name = 'verify-restored-orders'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-restored-orders.sql'; Expected = 'Expected/orders.csv'; Actual = 'orders.csv' }
        @{ Name = 'verify-restored-new-orders'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-restored-new-orders.sql'; Expected = 'Expected/new-orders.csv'; Actual = 'new-orders.csv' }
    )

    Cleanup = @()
}
