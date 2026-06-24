@{
    Name = 'LargeOnTimeBackupGc'
    Description = 'Debug-only large backup cleanup test using a 2000-2010 slice of the public ClickHouse OnTime dataset.'
    ExcludeFromRunAll = $true
    Resources = @(
        @{
            Name = 'server'
            Type = 'ChoboServer'
            Environment = @{
                Chobo__RetentionManagement__Interval = '12:00:00'
                Chobo__BackupsGarbageCollector__Interval = '12:00:00'
                Chobo__BackupStorageOperations__S3RequestTimeout = '00:10:00'
            }
        }
        @{ Name = 'source'; Type = 'SingleNode' }
        @{ Name = 'restore'; Type = 'SingleNode' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 7200

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
        @{ Name = 'create-and-load-ontime'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-and-load-ontime.sql' }
        @{ Name = 'summarize-ontime'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/summarize-ontime.sql' }
        @{ Name = 'optimize-ontime'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/optimize-ontime.sql' }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'large-ontime-source', '--mode', 'SingleInstance', '--host', '{source.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio-large-gc', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'large-ontime-restore', '--mode', 'SingleInstance', '--host', '{restore.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
        }
        @{
            Name = 'add-large-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'large-ontime-policy', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/LargeOnTimeBackupGc/selector-ontime.json', '--full-retention-minutes', '10080', '--incremental-retention-minutes', '10080', '--min-backups-to-keep', '1', '--min-full-backups-to-keep', '1')
            SaveJsonAs = 'policy'
        }
        @{
            Name = 'start-large-full-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Full')
            SaveJsonAs = 'fullBackup'
        }
        @{
            Name = 'wait-large-full-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{fullBackup.id}', '--timeout-seconds', '3600', '--poll-seconds', '10')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'backupType'; Equals = 'Full' }
                @{ Path = 'backupSizeBytes'; NotEmpty = $true }
                @{ Path = 'tables[0].status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].effectiveBackupType'; Equals = 'Full' }
                @{ Path = 'tables[0].backupSizeBytes'; NotEmpty = $true }
            )
        }
        @{
            Name = 'large-full-objects-exist-before-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 60
            RetryIntervalSeconds = 5
            ExpectTextContains = '{fullBackup.id.n}'
        }
        @{
            Name = 'start-noop-incremental-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{policy.id}', '--backup-type', 'Incremental')
            SaveJsonAs = 'incrementalBackup'
        }
        @{
            Name = 'wait-noop-incremental-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{incrementalBackup.id}', '--timeout-seconds', '3600', '--poll-seconds', '10')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'backupType'; Equals = 'Incremental' }
                @{ Path = 'backupSizeBytes'; NotEmpty = $true }
                @{ Path = 'tables[0].status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].effectiveBackupType'; Equals = 'Incremental' }
                @{ Path = 'tables[0].parentFullBackupId'; Equals = '{fullBackup.id}' }
                @{ Path = 'tables[0].backupSizeBytes'; NotEmpty = $true }
            )
        }
        @{
            Name = 'assert-noop-incremental-is-small'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-File', '/suite/Tests/LargeOnTimeBackupGc/assert-incremental-size.ps1', '{fullBackup.id}', '{incrementalBackup.id}')
            ExpectTextContains = @('fullBackupSizeBytes=', 'incrementalBackupSizeBytes=', 'incrementalRatio=')
        }
        @{
            Name = 'large-incremental-objects-exist-before-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 60
            RetryIntervalSeconds = 5
            ExpectTextContains = '{incrementalBackup.id.n}'
        }
        @{
            Name = 'show-large-full-progress'
            Type = 'Cli'
            Args = @('backups', 'progress', '--id', '{fullBackup.id}')
            ExpectTextContains = @('Backup {fullBackup.id} Succeeded', 'large_ontime_source.ontime', 'size=')
        }
        @{
            Name = 'show-large-incremental-progress'
            Type = 'Cli'
            Args = @('backups', 'progress', '--id', '{incrementalBackup.id}')
            ExpectTextContains = @('Backup {incrementalBackup.id} Succeeded', 'large_ontime_source.ontime', 'size=')
        }
        @{
            Name = 'restore-noop-incremental'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{incrementalBackup.id}', '--target-cluster-id', '{restoreCluster.id}')
            SaveJsonAs = 'restoreRun'
        }
        @{
            Name = 'wait-noop-incremental-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restoreRun.id}', '--timeout-seconds', '3600', '--poll-seconds', '10')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'assert-restored-large-table-matches-source'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-File', '/suite/Tests/LargeOnTimeBackupGc/assert-ontime-restored.ps1', '{source.Host}', '{restore.Host}')
            ExpectTextContains = @('source=', 'restored=')
        }
        @{
            Name = 'request-incremental-delete'
            Type = 'Cli'
            Args = @('backups', 'delete', '--id', '{incrementalBackup.id}', '--confirm-destructive')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleteRequested' }
            )
        }
        @{
            Name = 'run-gc-for-incremental-delete'
            Type = 'Cli'
            Args = @('gc', 'run-one', '--id', '{incrementalBackup.id}')
            ExpectJson = @(
                @{ Path = 'lastPendingCleanupCount'; Equals = 1 }
                @{ Path = 'lastCleanedCount'; Equals = 1 }
                @{ Path = 'lastFailedCount'; Equals = 0 }
            )
        }
        @{
            Name = 'wait-incremental-deleted'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{incrementalBackup.id}', '--timeout-seconds', '3600', '--poll-seconds', '10')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleted' }
                @{ Path = 'deletedAt'; NotEmpty = $true }
            )
        }
        @{
            Name = 'incremental-objects-gone-after-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 120
            RetryIntervalSeconds = 5
            ExpectTextNotContains = '{incrementalBackup.id.n}'
        }
        @{
            Name = 'full-objects-still-exist-after-incremental-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 60
            RetryIntervalSeconds = 5
            ExpectTextContains = '{fullBackup.id.n}'
        }
        @{
            Name = 'request-full-delete'
            Type = 'Cli'
            Args = @('backups', 'delete', '--id', '{fullBackup.id}', '--confirm-destructive')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleteRequested' }
            )
        }
        @{
            Name = 'run-gc-for-full-delete'
            Type = 'Cli'
            Args = @('gc', 'run-one', '--id', '{fullBackup.id}')
            ExpectJson = @(
                @{ Path = 'lastPendingCleanupCount'; Equals = 1 }
                @{ Path = 'lastCleanedCount'; Equals = 1 }
                @{ Path = 'lastFailedCount'; Equals = 0 }
            )
        }
        @{
            Name = 'wait-full-deleted'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{fullBackup.id}', '--timeout-seconds', '3600', '--poll-seconds', '10')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleted' }
                @{ Path = 'deletedAt'; NotEmpty = $true }
            )
        }
        @{
            Name = 'full-objects-gone-after-delete'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 120
            RetryIntervalSeconds = 5
            ExpectTextNotContains = '{fullBackup.id.n}'
        }
        @{
            Name = 'show-gc-status'
            Type = 'Cli'
            Args = @('gc', 'status')
            ExpectJson = @(
                @{ Path = 'isRunning'; Equals = $false }
                @{ Path = 'lastFailedCount'; Equals = 0 }
            )
        }
        @{
            Name = 'show-gc-logs'
            Type = 'Cli'
            Args = @('logs', 'show', '--last', '2000')
            ExpectTextContains = @('S3 directory', 'Backup cleanup completed')
        }
    )
    Verify = @()
    Cleanup = @()
}

