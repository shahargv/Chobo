@{
    Name = 'IncrementalMultipleFullParents'
    Description = 'Verifies one incremental backup can depend on multiple full backup runs, retention preserves both parents, and manual parent deletion cascades to the dependent incremental.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'SingleNode' }
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
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'add-policy-orders'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'incremental-multi-parent', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/IncrementalMultipleFullParents/selector-orders.json', '--full-retention-minutes', '10080', '--incremental-retention-minutes', '10080', '--min-backups-to-keep', '0', '--min-full-backups-to-keep', '0')
            SaveJsonAs = 'policy'
        }
        @{
            Name = 'start-orders-full'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Full')
            SaveJsonAs = 'ordersFull'
        }
        @{
            Name = 'wait-orders-full'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{ordersFull.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 1 }
                @{ Path = 'tables'; ContainsObject = @{ table = 'orders'; effectiveBackupType = 'Full' } }
            )
        }
        @{
            Name = 'update-policy-invoices'
            Type = 'Cli'
            Args = @('policies', 'update', '--id', '{policy.id}', '--name', 'incremental-multi-parent', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/IncrementalMultipleFullParents/selector-invoices.json', '--full-retention-minutes', '10080', '--incremental-retention-minutes', '10080', '--min-backups-to-keep', '0', '--min-full-backups-to-keep', '0')
        }
        @{
            Name = 'start-invoices-full'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Full')
            SaveJsonAs = 'invoicesFull'
        }
        @{
            Name = 'wait-invoices-full'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{invoicesFull.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 1 }
                @{ Path = 'tables'; ContainsObject = @{ table = 'invoices'; effectiveBackupType = 'Full' } }
            )
        }
        @{ Name = 'mutate-source-data'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/mutate-source-data.sql' }
        @{
            Name = 'update-policy-all'
            Type = 'Cli'
            Args = @('policies', 'update', '--id', '{policy.id}', '--name', 'incremental-multi-parent', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/IncrementalMultipleFullParents/selector-all.json', '--full-retention-minutes', '10080', '--incremental-retention-minutes', '10080', '--min-backups-to-keep', '0', '--min-full-backups-to-keep', '0')
        }
        @{
            Name = 'start-incremental'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Incremental')
            SaveJsonAs = 'incrementalBackup'
        }
        @{
            Name = 'wait-incremental'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{incrementalBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'backupType'; Equals = 'Incremental' }
                @{ Path = 'relatedFullBackupIds'; Count = 2 }
                @{ Path = 'relatedFullBackupIds'; Contains = '{ordersFull.id}' }
                @{ Path = 'relatedFullBackupIds'; Contains = '{invoicesFull.id}' }
                @{ Path = 'tables'; ContainsObject = @{ table = 'orders'; effectiveBackupType = 'Incremental'; parentFullBackupId = '{ordersFull.id}' } }
                @{ Path = 'tables'; ContainsObject = @{ table = 'invoices'; effectiveBackupType = 'Incremental'; parentFullBackupId = '{invoicesFull.id}' } }
            )
        }
        @{
            Name = 'progress-shows-plural-related-full-backups'
            Type = 'Cli'
            Args = @('backups', 'progress', '--id', '{incrementalBackup.id}')
            ExpectTextContains = @('relatedFullBackups=', '{ordersFull.id}', '{invoicesFull.id}')
            ExpectTextNotContains = 'relatedFullBackup='
        }
        @{
            Name = 'shorten-full-retention-after-incremental'
            Type = 'Cli'
            Args = @('policies', 'update', '--id', '{policy.id}', '--name', 'incremental-multi-parent', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/IncrementalMultipleFullParents/selector-all.json', '--full-retention-minutes', '1', '--incremental-retention-minutes', '10080', '--min-backups-to-keep', '0', '--min-full-backups-to-keep', '0')
        }
        @{
            Name = 'wait-until-full-retention-age'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', 'Start-Sleep -Seconds 70')
        }
        @{
            Name = 'retention-keeps-orders-parent'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{ordersFull.id}')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'retention-keeps-invoices-parent'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{invoicesFull.id}')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'manual-delete-orders-parent'
            Type = 'Cli'
            Args = @('backups', 'delete', '--id', '{ordersFull.id}', '--confirm-destructive')
            ExpectJson = @(
                @{ Path = 'status'; Contains = 'ManualDelete' }
            )
        }
        @{
            Name = 'dependent-incremental-delete-cascaded'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{incrementalBackup.id}')
            RetryTimeoutSeconds = 15
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Contains = 'ManualDelete' }
                @{ Path = 'deletionReason'; Equals = 'manual-parent' }
            )
        }
        @{
            Name = 'unrelated-parent-still-live'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{invoicesFull.id}')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
    )

    Verify = @()
    Cleanup = @()
}