@{
    Name = 'CompressedBackupSingleNode'
    Description = 'Creates and restores an LZMA level 3 ZIP backup without password protection.'
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
            ExpectJson = @(@{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } })
        }
        @{
            Name = 'auth-profile'
            Type = 'Cli'
            Args = @('server', 'auth', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            ExpectTextContains = 'Authenticated to http://choboserver:8080.'
        }
        @{ Name = 'create-source-table'; Type = 'Sql'; Resource = 'source'; Path = '../BackupRestoreSingleNode/Sql/create-source-table.sql' }
        @{ Name = 'insert-source-rows'; Type = 'Sql'; Resource = 'source'; Path = '../BackupRestoreSingleNode/Sql/insert-source-rows.sql' }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'SingleInstance', '--host', '{source.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(@{ Path = 'mode'; Equals = 'SingleInstance' })
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
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'add-compression-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'single-node-lzma', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreSingleNode/selector-all.json', '--compression-method', 'lzma', '--compression-level', '3')
            SaveJsonAs = 'compressionPolicy'
            ExpectJson = @(
                @{ Path = 'passwordMode'; Equals = 'None' }
                @{ Path = 'compressionMethod'; Equals = 'Lzma' }
                @{ Path = 'compressionLevel'; Equals = 3 }
            )
        }
        @{
            Name = 'start-compressed-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{compressionPolicy.id}')
            SaveJsonAs = 'backup'
            ExpectJson = @(@{ Path = 'status'; Equals = 'Queued' })
        }
        @{
            Name = 'wait-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{backup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' })
        }
        @{
            Name = 'show-compressed-backup'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{backup.id}')
            ExpectJson = @(
                @{ Path = 'encryptionState'; Equals = 'Unencrypted' }
                @{ Path = 'compressionMethod'; Equals = 'Lzma' }
                @{ Path = 'compressionLevel'; Equals = 3 }
                @{ Path = 'clickHouseBackupSettings.compression_method'; Equals = 'lzma' }
                @{ Path = 'clickHouseBackupSettings.compression_level'; Equals = 3 }
                @{ Path = 'tables[0].shards[0].isPasswordProtected'; Equals = $false }
                @{ Path = 'tables[0].shards[0].storagePath'; Contains = '.zip' }
            )
        }
        @{
            Name = 'add-combined-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'single-node-combined', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreSingleNode/selector-all.json', '--password-mode', 'constant', '--backup-password', 'Combined-System-Password!', '--compression-method', 'lzma', '--compression-level', '3')
            SaveJsonAs = 'combinedPolicy'
            ExpectJson = @(
                @{ Path = 'passwordMode'; Equals = 'Constant' }
                @{ Path = 'compressionMethod'; Equals = 'Lzma' }
                @{ Path = 'compressionLevel'; Equals = 3 }
            )
            ExpectTextNotContains = 'Combined-System-Password!'
        }
        @{
            Name = 'start-combined-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{combinedPolicy.id}')
            SaveJsonAs = 'combinedBackup'
        }
        @{
            Name = 'wait-combined-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{combinedBackup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' })
        }
        @{
            Name = 'show-combined-backup'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{combinedBackup.id}')
            ExpectJson = @(
                @{ Path = 'encryptionState'; Equals = 'EncryptedKeyAvailable' }
                @{ Path = 'compressionMethod'; Equals = 'Lzma' }
                @{ Path = 'compressionLevel'; Equals = 3 }
                @{ Path = 'tables[0].shards[0].isPasswordProtected'; Equals = $true }
                @{ Path = 'tables[0].shards[0].storagePath'; Contains = '.zip' }
            )
            ExpectTextNotContains = 'Combined-System-Password!'
        }
        @{
            Name = 'start-restore'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_single_source', '--table', 'source_orders', '--target-database', 'backup_single_restore', '--target-table', 'restored_orders')
            SaveJsonAs = 'restoreRun'
            ExpectJson = @(@{ Path = 'status'; Equals = 'Queued' })
        }
        @{
            Name = 'wait-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restoreRun.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(@{ Path = 'status'; Equals = 'Succeeded' })
        }
    )

    Verify = @(
        @{
            Name = 'verify-restored-rows'
            Type = 'Csv'
            Resource = 'restore'
            Path = '../BackupRestoreSingleNode/Sql/select-restored-rows.sql'
            Expected = '../BackupRestoreSingleNode/Expected/restored.csv'
            Actual = 'restored.csv'
        }
    )
    Cleanup = @()
}
