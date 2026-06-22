@{
    Name = 'BackupRestoreCancellation'
    Description = 'Cancels running backup and restore operations, verifies ClickHouse operations are killed, backup remains are garbage-collected, and audit entries are written.'

    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'SingleNode' }
        @{ Name = 'restore'; Type = 'SingleNode' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'cancel-s3' }
    )

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
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'SingleInstance', '--host', '{source.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @( @{ Path = 'id'; NotEmpty = $true } )
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore', '--mode', 'SingleInstance', '--host', '{restore.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
            ExpectJson = @( @{ Path = 'id'; NotEmpty = $true } )
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
            ExpectJson = @( @{ Path = 'id'; NotEmpty = $true } )
        }
        @{
            Name = 'start-good-backup-for-restore-cancel'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreCancellation/selector-orders.json')
            SaveJsonAs = 'goodBackup'
            ExpectJson = @( @{ Path = 'id'; NotEmpty = $true } )
        }
        @{
            Name = 'wait-good-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{goodBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @( @{ Path = 'status'; Equals = 'Succeeded' } )
        }
        @{
            Name = 'delay-cancel-backup-after-operation-persisted'
            Type = 'Cli'
            Args = @('test-hooks', 'delay-next-backup-before-poll')
            ExpectJson = @( @{ Path = 'delayed'; Equals = 'backup-before-poll' } )
        }
        @{
            Name = 'start-backup-to-cancel'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreCancellation/selector-orders.json')
            SaveJsonAs = 'canceledBackup'
            ExpectJson = @( @{ Path = 'id'; NotEmpty = $true } )
        }
        @{
            Name = 'wait-cancel-backup-operation-persisted'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{canceledBackup.id}')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Running' }
                @{ Path = 'tables[0].shards[0].clickHouseOperationId'; NotEmpty = $true }
            )
        }
        @{
            Name = 'cancel-backup'
            Type = 'Cli'
            Args = @('backups', 'cancel', '--id', '{canceledBackup.id}')
            ExpectJson = @( @{ Path = 'status'; Equals = 'Canceled' } )
        }
        @{
            Name = 'gc-status-before-manual-run'
            Type = 'Cli'
            Args = @('gc', 'status')
            ExpectJson = @( @{ Path = 'currentRunReason'; NotEmpty = $true } )
        }
        @{
            Name = 'gc-queue-shows-canceled-backup'
            Type = 'Cli'
            Args = @('gc', 'queue')
            RetryTimeoutSeconds = 20
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ entityId = '{canceledBackup.id}'; entityType = 'backup'; status = 'Canceled'; reason = 'canceled-backup-garbage-collector' } }
            )
        }
        @{
            Name = 'manual-run-one-backup-gc'
            Type = 'Cli'
            Args = @('gc', 'run-one', '--id', '{canceledBackup.id}')
            RetryTimeoutSeconds = 20
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'lastCompletedAt'; NotEmpty = $true }
                @{ Path = 'lastFailedCount'; Equals = 0 }
            )
        }
        @{
            Name = 'wait-canceled-backup-cleaned'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{canceledBackup.id}')
            RetryTimeoutSeconds = 45
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Canceled' }
                @{ Path = 'deletedAt'; NotEmpty = $true }
            )
        }

        @{
            Name = 'delay-cancel-restore-after-operation-persisted'
            Type = 'Cli'
            Args = @('test-hooks', 'delay-next-restore-before-poll')
            ExpectJson = @( @{ Path = 'delayed'; Equals = 'restore-before-poll' } )
        }
        @{
            Name = 'start-restore-to-cancel'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{goodBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_cancel', '--table', 'orders', '--target-database', 'backup_cancel_restore', '--target-table', 'orders')
            SaveJsonAs = 'canceledRestore'
            ExpectJson = @( @{ Path = 'id'; NotEmpty = $true } )
        }
        @{
            Name = 'wait-cancel-restore-operation-persisted'
            Type = 'Cli'
            Args = @('restores', 'show', '--id', '{canceledRestore.id}')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Running' }
                @{ Path = 'tables[0].shards[0].clickHouseOperationId'; NotEmpty = $true }
            )
        }
        @{
            Name = 'cancel-restore'
            Type = 'Cli'
            Args = @('restores', 'cancel', '--id', '{canceledRestore.id}')
            ExpectJson = @( @{ Path = 'status'; Equals = 'Canceled' } )
        }
        @{
            Name = 'audit-shows-cancellations'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '200')
            RetryTimeoutSeconds = 10
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'canceled'; entityType = 'backup'; entityId = '{canceledBackup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'canceled'; entityType = 'restore'; entityId = '{canceledRestore.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'backup-cleanup-succeeded'; entityType = 'backup'; entityId = '{canceledBackup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'manual-run-one-requested'; entityType = 'backup-garbage-collector'; entityId = '{canceledBackup.id}' } }
            )
        }
    )

    Verify = @()
    Cleanup = @()
}




