@{
    Name = 'BackupRetentionCleanup'
    Description = 'Verifies policy retention, pinning, manual deletion, failed cleanup, restart resume, audit, and actual S3 object deletion.'
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
        @{ Name = 'create-source-table'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-table.sql' }
        @{ Name = 'insert-source-rows'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/insert-source-rows.sql' }
        @{
            Name = 'mc-alias'
            Type = 'Shell'
            Args = @('mc', 'alias', 'set', 'chobo', 'http://minio:9000', '{backupStore.AccessKey}', '{backupStore.SecretKey}')
            ExpectTextContains = 'Added'
        }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'SingleInstance', '--host', '{source.Host}')
            SaveJsonAs = 'sourceCluster'
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
        }
        @{
            Name = 'add-retention-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'retention', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRetentionCleanup/selector-all.json', '--full-retention-minutes', '1', '--incremental-retention-minutes', '1', '--min-backups-to-keep', '1', '--min-full-backups-to-keep', '1', '--failed-backup-retention-mode', 'KeepAndExcludeFromMinBackupsToKeep')
            SaveJsonAs = 'retentionPolicy'
            ExpectJson = @(
                @{ Path = 'retention.fullRetentionMinutes'; Equals = 1 }
                @{ Path = 'retention.incrementalRetentionMinutes'; Equals = 1 }
                @{ Path = 'retention.minBackupsToKeep'; Equals = 1 }
                @{ Path = 'retention.minFullBackupsToKeep'; Equals = 1 }
            )
        }
        @{
            Name = 'add-fast-schedule'
            Type = 'Cli'
            Args = @('schedules', 'add', '--name', 'fast-retention', '--policy-id', '{retentionPolicy.id}', '--backup-type', 'Full', '--cron', '0/2 * * * * ?', '--timezone', 'UTC')
            SaveJsonAs = 'schedule'
        }
        @{
            Name = 'wait-two-scheduled-successes'
            Type = 'Cli'
            Args = @('backups', 'list', '--policy-id', '{retentionPolicy.id}', '--status', 'Succeeded')
            RetryTimeoutSeconds = 60
            RetryIntervalSeconds = 2
            SaveJsonAs = 'scheduledBackups'
            ExpectJson = @(
                @{ Path = '[1].id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'pin-older-backup'
            Type = 'Cli'
            Args = @('backups', 'pin', '--id', '{scheduledBackups.1.id}')
            ExpectJson = @(
                @{ Path = 'isPinned'; Equals = $true }
            )
        }
        @{
            Name = 'dashboard-shows-pin'
            Type = 'Cli'
            Args = @('dashboard', 'show')
            ExpectTextContains = @('pinned=')
        }
        @{
            Name = 'wait-until-retention-age'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', 'Start-Sleep -Seconds 70')
        }
        @{
            Name = 'unpin-older-backup'
            Type = 'Cli'
            Args = @('backups', 'unpin', '--id', '{scheduledBackups.1.id}')
            ExpectJson = @(
                @{ Path = 'isPinned'; Equals = $false }
            )
        }
        @{
            Name = 'wait-retention-deletes-older'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{scheduledBackups.1.id}')
            RetryTimeoutSeconds = 45
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'BackupExpiredDeleted' }
                @{ Path = 'deletedAt'; NotEmpty = $true }
            )
        }
        @{
            Name = 'start-manual-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRetentionCleanup/selector-all.json')
            SaveJsonAs = 'manualBackup'
        }
        @{
            Name = 'wait-manual-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{manualBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'manual-delete'
            Type = 'Cli'
            Args = @('backups', 'delete', '--id', '{manualBackup.id}')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleteRequested' }
            )
        }
        @{
            Name = 'wait-manual-deleted'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{manualBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'ManualDeleted' }
            )
        }
        @{
            Name = 'manual-backup-s3-objects-gone'
            Type = 'Shell'
            Args = @('mc', 'ls', '--recursive', 'chobo/{backupStore.Bucket}')
            RetryTimeoutSeconds = 15
            RetryIntervalSeconds = 1
            ExpectTextNotContains = '{manualBackup.id.n}'
        }
        @{
            Name = 'restore-deleted-backup-rejected'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{manualBackup.id}', '--target-cluster-id', '{sourceCluster.id}', '--database', 'backup_retention_source', '--table', 'orders')
            ExpectExitCode = 1
            ExpectTextContains = 'Deleted or delete-pending backups cannot be restored.'
        }
        @{
            Name = 'show-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '500')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ action = 'pin'; entityType = 'backup' } }
                @{ Path = '$'; ContainsObject = @{ action = 'unpin'; entityType = 'backup' } }
                @{ Path = '$'; ContainsObject = @{ action = 'delete-requested'; entityType = 'backup' } }
                @{ Path = '$'; ContainsObject = @{ action = 'backup-retention-delete-requested'; entityType = 'backup' } }
                @{ Path = '$'; ContainsObject = @{ action = 'backup-cleanup-succeeded'; entityType = 'backup' } }
            )
        }
    )

    Verify = @()
    Cleanup = @()
}
