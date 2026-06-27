@{
    Name = 'FailureScenario'
    Description = 'Exercises named backup and restore failure modes and verifies run records, audit, logs, and dashboard output stay diagnostic.'

    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'single'; Type = 'SingleNode' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 1; DnsName = 'failure-source-cluster' }
        @{ Name = 'restore'; Type = 'SingleNode' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'failure-s3' }
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
        @{ Name = 'create-single-source-table'; Type = 'Sql'; Resource = 'single'; Path = 'Sql/create-single-source-table.sql' }
        @{ Name = 'create-sharded-source-table'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-sharded-source-table.sql' }
        @{ Name = 'insert-source-shard-two'; Type = 'Sql'; Resource = 'source'; Host = 'clickhouse-source-s2-r1'; Query = "INSERT INTO backup_failure_sharded.orders_local (id, shard, name) VALUES (101, 2, 's2-a'), (102, 2, 's2-b')" }
    )

    Action = @(
        @{
            Name = 'add-single-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'single', '--mode', 'SingleInstance', '--host', '{single.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'singleCluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-single-cluster-wrong-credentials'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'single-wrong-credentials', '--mode', 'SingleInstance', '--host', '{single.Host}', '--username', 'default', '--password', 'definitely-wrong', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'wrongCredentialCluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-source-down-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source-down', '--mode', 'SingleInstance', '--host', 'missing-source-clickhouse', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceDownCluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'Cluster', '--node', 'clickhouse-source-s1-r1:9000', '--clickhouse-cluster-name', '{source.ClusterName}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-restore-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore', '--mode', 'SingleInstance', '--host', '{restore.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreCluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-restore-down-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore-down', '--mode', 'SingleInstance', '--host', 'missing-dest-clickhouse', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreDownCluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-restore-wrong-credentials'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'restore-wrong-credentials', '--mode', 'SingleInstance', '--host', '{restore.Host}', '--username', 'default', '--password', 'definitely-wrong', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'restoreWrongCredentialCluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-good-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'good-minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'goodTarget'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-bad-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'bad-minio-credentials', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', 'wrong-access-key', '--secret-key', 'wrong-secret-key', '--force-path-style')
            SaveJsonAs = 'badTarget'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-unavailable-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'missing-minio', '--endpoint', 'http://missing-minio:9000', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'unavailableTarget'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'start-source-down-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceDownCluster.id}', '--target-id', '{goodTarget.id}', '--selector-file', '/suite/Tests/FailureScenario/selector-single-orders.json')
            SaveJsonAs = 'sourceDownBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-source-down-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{sourceDownBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; NotEmpty = $true }
            )
        }
        @{
            Name = 'start-source-auth-failure-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{wrongCredentialCluster.id}', '--target-id', '{goodTarget.id}', '--selector-file', '/suite/Tests/FailureScenario/selector-single-orders.json')
            SaveJsonAs = 'authFailureBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-source-auth-failure-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{authFailureBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; NotEmpty = $true }
            )
        }
        @{
            Name = 'delay-backup-after-operation-persisted'
            Type = 'Cli'
            Args = @('test-hooks', 'delay-next-backup-before-poll')
            ExpectJson = @(
                @{ Path = 'delayed'; Equals = 'backup-before-poll' }
            )
        }
        @{
            Name = 'start-backup-before-server-restart'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{singleCluster.id}', '--target-id', '{goodTarget.id}', '--selector-file', '/suite/Tests/FailureScenario/selector-single-orders.json')
            SaveJsonAs = 'restartBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-backup-operation-persisted-before-restart'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{restartBackup.id}')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Running' }
                @{ Path = 'tables[0].shards[0].clickHouseOperationId'; NotEmpty = $true }
            )
        }
        @{
            Name = 'crash-server-during-backup'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', '$output = ChoboCli test-hooks crash 2>&1; $text = ($output | Out-String); if ($LASTEXITCODE -eq 0 -and $text.Contains(''crashing'')) { ''crash-ok''; exit 0 }; if ($text.Contains(''Error while copying content to a stream'')) { ''crash-ok''; exit 0 }; throw (''unexpected crash command result: '' + $text)')
            ExpectTextContains = 'crash-ok'
        }
        @{
            Name = 'wait-backup-after-server-restart'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{restartBackup.id}', '--timeout-seconds', '90', '--poll-seconds', '1')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'start-unavailable-s3-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{singleCluster.id}', '--target-id', '{unavailableTarget.id}', '--selector-file', '/suite/Tests/FailureScenario/selector-single-orders.json')
            SaveJsonAs = 'unavailableS3Backup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-unavailable-s3-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{unavailableS3Backup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; NotEmpty = $true }
            )
        }
        @{
            Name = 'start-sharded-bad-s3-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{badTarget.id}', '--selector-file', '/suite/Tests/FailureScenario/selector-sharded-orders.json')
            SaveJsonAs = 'badS3Backup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-sharded-bad-s3-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{badS3Backup.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; Contains = 'shard 1' }
                @{ Path = 'failureReason'; Contains = 'shard 2' }
                @{ Path = 'tables[0].error'; Contains = 'Shard 1' }
                @{ Path = 'tables[0].error'; Contains = 'Shard 2' }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; status = 'Failed' } }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 2; status = 'Failed' } }
            )
        }
        @{
            Name = 'start-good-backup-for-restore-failure'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{singleCluster.id}', '--target-id', '{goodTarget.id}', '--selector-file', '/suite/Tests/FailureScenario/selector-single-orders.json')
            SaveJsonAs = 'goodBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-good-backup-for-restore-failure'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{goodBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'delay-restore-after-operation-persisted'
            Type = 'Cli'
            Args = @('test-hooks', 'delay-next-restore-before-poll')
            ExpectJson = @(
                @{ Path = 'delayed'; Equals = 'restore-before-poll' }
            )
        }
        @{
            Name = 'start-restore-before-server-restart'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{goodBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_failure_single', '--table', 'orders', '--target-database', 'backup_failure_restore_restart', '--target-table', 'orders')
            SaveJsonAs = 'restartRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-restore-operation-persisted-before-restart'
            Type = 'Cli'
            Args = @('restores', 'show', '--id', '{restartRestore.id}')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Running' }
                @{ Path = 'tables[0].shards[0].clickHouseOperationId'; NotEmpty = $true }
            )
        }
        @{
            Name = 'crash-server-during-restore'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', '$output = ChoboCli test-hooks crash 2>&1; $text = ($output | Out-String); if ($LASTEXITCODE -eq 0 -and $text.Contains(''crashing'')) { ''crash-ok''; exit 0 }; if ($text.Contains(''Error while copying content to a stream'')) { ''crash-ok''; exit 0 }; throw (''unexpected crash command result: '' + $text)')
            ExpectTextContains = 'crash-ok'
        }
        @{
            Name = 'wait-restore-after-server-restart'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{restartRestore.id}', '--timeout-seconds', '90', '--poll-seconds', '1')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'start-dest-down-restore'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{goodBackup.id}', '--target-cluster-id', '{restoreDownCluster.id}', '--database', 'backup_failure_single', '--table', 'orders', '--target-database', 'backup_failure_dest_down', '--target-table', 'orders')
            SaveJsonAs = 'destDownRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-dest-down-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{destDownRestore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; NotEmpty = $true }
            )
        }
        @{
            Name = 'start-dest-auth-failure-restore'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{goodBackup.id}', '--target-cluster-id', '{restoreWrongCredentialCluster.id}', '--database', 'backup_failure_single', '--table', 'orders', '--target-database', 'backup_failure_dest_auth', '--target-table', 'orders')
            SaveJsonAs = 'destAuthFailureRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-dest-auth-failure-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{destAuthFailureRestore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; NotEmpty = $true }
            )
        }
        @{
            Name = 'make-good-target-unavailable'
            Type = 'Cli'
            Args = @('targets', 'update-s3', '--id', '{goodTarget.id}', '--name', 'good-minio-now-unavailable', '--endpoint', 'http://missing-minio:9000', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{goodTarget.id}' }
            )
        }
        @{
            Name = 'start-restore-with-unavailable-s3'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{goodBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_failure_single', '--table', 'orders', '--target-database', 'backup_failure_restore_s3_down', '--target-table', 'orders')
            SaveJsonAs = 'unavailableS3Restore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-restore-with-unavailable-s3'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{unavailableS3Restore.id}', '--timeout-seconds', '60', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; NotEmpty = $true }
            )
        }
        @{
            Name = 'make-good-target-credentials-wrong'
            Type = 'Cli'
            Args = @('targets', 'update-s3', '--id', '{goodTarget.id}', '--name', 'good-minio-now-wrong', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', 'wrong-access-key', '--secret-key', 'wrong-secret-key', '--force-path-style')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{goodTarget.id}' }
            )
        }
        @{
            Name = 'start-restore-with-bad-s3-credentials'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{goodBackup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_failure_single', '--table', 'orders', '--target-database', 'backup_failure_restore', '--target-table', 'orders')
            SaveJsonAs = 'badS3Restore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-restore-with-bad-s3-credentials'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{badS3Restore.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; NotEmpty = $true }
                @{ Path = 'tables[0].error'; NotEmpty = $true }
                @{ Path = 'tables[0].shards'; ContainsObject = @{ sourceShardNumber = 1; status = 'Failed' } }
            )
        }
        @{
            Name = 'show-backup-failures'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{badS3Backup.id}')
            ExpectJson = @(
                @{ Path = 'failureReason'; Contains = 'shard 1' }
                @{ Path = 'failureReason'; Contains = 'shard 2' }
            )
        }
        @{
            Name = 'show-restore-failure'
            Type = 'Cli'
            Args = @('restores', 'show', '--id', '{badS3Restore.id}')
            ExpectJson = @(
                @{ Path = 'failureReason'; NotEmpty = $true }
            )
        }
        @{
            Name = 'seed-missing-clickhouse-operation'
            Type = 'Cli'
            Args = @('test-hooks', 'seed-missing-backup-operation', '--source-cluster-id', '{singleCluster.id}', '--target-id', '{goodTarget.id}', '--database', 'backup_failure_single', '--table', 'orders')
            SaveJsonAs = 'missingOperationBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 'tables[0].shards[0].clickHouseOperationId'; Contains = 'missing-from-system-backups' }
            )
        }
        @{
            Name = 'wait-missing-clickhouse-operation'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{missingOperationBackup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'failureReason'; Contains = 'missing from system.backups' }
            )
        }
        @{
            Name = 'add-failing-scheduled-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'scheduled-bad-s3', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{badTarget.id}', '--selector-file', '/suite/Tests/FailureScenario/selector-sharded-orders.json')
            SaveJsonAs = 'scheduledBadS3Policy'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-failing-schedule'
            Type = 'Cli'
            Args = @('schedules', 'add', '--name', 'scheduled-bad-s3', '--policy-id', '{scheduledBadS3Policy.id}', '--backup-type', 'Full', '--cron', '0/1 * * * * ?', '--timezone', 'UTC')
            SaveJsonAs = 'scheduledBadS3Schedule'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'dashboard-shows-failure-text'
            Type = 'Cli'
            Args = @('dashboard', 'show')
            RetryTimeoutSeconds = 45
            RetryIntervalSeconds = 2
            ExpectTextContains = @('Chobo dashboard', 'scheduled-bad-s3', 'Failed', 'failure=')
        }
        @{
            Name = 'audit-shows-failures'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '500')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'backup'; entityId = '{authFailureBackup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'backup'; entityId = '{sourceDownBackup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'backup'; entityId = '{unavailableS3Backup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'backup'; entityId = '{badS3Backup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'backup'; entityId = '{missingOperationBackup.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'restore'; entityId = '{destDownRestore.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'restore'; entityId = '{destAuthFailureRestore.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'restore'; entityId = '{unavailableS3Restore.id}' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'restore'; entityId = '{badS3Restore.id}' } }
            )
            ExpectTextContains = @('failureReason')
        }
        @{
            Name = 'logs-show-failures'
            Type = 'Cli'
            Args = @('logs', 'show', '--last', '500', '--severity', 'warning,error')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectTextContains = @('{sourceDownBackup.id}', '{authFailureBackup.id}', '{unavailableS3Backup.id}', '{badS3Backup.id}', '{destDownRestore.id}', '{destAuthFailureRestore.id}', '{unavailableS3Restore.id}', '{badS3Restore.id}', '{missingOperationBackup.id}', 'Failure reason')
            ExpectTextNotContains = @('{backupStore.AccessKey}', '{backupStore.SecretKey}', 'wrong-access-key', 'wrong-secret-key')
        }
        @{
            Name = 'logs-operation-paging'
            Type = 'Cli'
            Args = @('logs', 'show', '--operation-id', '{badS3Backup.id}', '--offset', '0', '--limit', '1')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'offset'; Equals = 0 }
                @{ Path = 'limit'; Equals = 1 }
                @{ Path = 'totalCount'; NotEmpty = $true }
                @{ Path = 'items'; Count = 1 }
            )
            ExpectTextContains = @('{badS3Backup.id}')
            ExpectTextNotContains = @('{sourceDownBackup.id}', '{backupStore.AccessKey}', '{backupStore.SecretKey}', 'wrong-access-key', 'wrong-secret-key')
        }
        @{
            Name = 'audit-operation-paging'
            Type = 'Cli'
            Args = @('audit', 'show', '--operation-id', '{badS3Backup.id}', '--offset', '0', '--limit', '2')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'offset'; Equals = 0 }
                @{ Path = 'limit'; Equals = 2 }
                @{ Path = 'totalCount'; NotEmpty = $true }
                @{ Path = 'items'; ContainsObject = @{ entityType = 'backup'; entityId = '{badS3Backup.id}' } }
            )
            ExpectTextContains = @('{badS3Backup.id}')
            ExpectTextNotContains = @('{sourceDownBackup.id}')
        }
    )

    Verify = @()
}


