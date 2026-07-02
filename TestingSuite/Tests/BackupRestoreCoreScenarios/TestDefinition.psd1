@{
    Name = 'BackupRestoreCoreScenarios'
    Description = 'Exercises core single-node backup and restore scenarios across rename, full database, append, schema mismatch, and schema-only engines.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'SingleNode' }
        @{ Name = 'restore'; Type = 'SingleNode' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )
    TimeoutSeconds = 120

    Setup = @(
        @{
            Name = 'wait-server-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 60
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } }
            )
        }
        @{
            Name = 'partial-schedule-add-shows-all-required-options'
            Type = 'Cli'
            Args = @('schedules', 'add')
            ExpectExitCode = 1
            ExpectTextContains = @('Missing required options: --name, --policy-id, --cron.')
        }
        @{
            Name = 'auth-profile'
            Type = 'Cli'
            Args = @('server', 'auth', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            ExpectTextContains = 'Authenticated to http://choboserver:8080.'
        }
        @{ Name = 'create-source-data'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-data.sql' }
        @{ Name = 'create-restore-targets'; Type = 'Sql'; Resource = 'restore'; Path = 'Sql/create-restore-targets.sql' }
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
            Name = 'default-schema-policy-created'
            Type = 'Cli'
            Args = @('policies', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'Daily schema snapshot - source'; contentMode = 'SchemaOnly'; isSystemDefault = $true } }
            )
        }
        @{
            Name = 'default-schema-schedule-created'
            Type = 'Cli'
            Args = @('schedules', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'Daily schema snapshot - source'; backupType = 'Full'; isSystemDefault = $true } }
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
            Name = 'add-user-schema-only-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'schema-only-all', '--source-cluster-id', '{sourceCluster.id}', '--schema-only')
            SaveJsonAs = 'schemaOnlyPolicy'
            ExpectJson = @(
                @{ Path = 'contentMode'; Equals = 'SchemaOnly' }
                @{ Path = 'isSystemDefault'; Equals = $false }
            )
        }
        @{
            Name = 'schema-only-incremental-schedule-rejected'
            Type = 'Cli'
            Args = @('schedules', 'add', '--name', 'bad-schema-incremental', '--policy-id', '{schemaOnlyPolicy.id}', '--backup-type', 'Incremental', '--cron', '0 0 3 * * ?', '--timezone', 'UTC')
            ExpectExitCode = 1
            ExpectTextContains = 'Schema-only policies cannot use incremental schedules.'
        }
        @{
            Name = 'start-schema-only-policy-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--policy-id', '{schemaOnlyPolicy.id}')
            SaveJsonAs = 'schemaOnlyBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 'contentMode'; Equals = 'SchemaOnly' }
                @{ Path = 'backupType'; Equals = 'Full' }
            )
        }
        @{
            Name = 'wait-schema-only-policy-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{schemaOnlyBackup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            SaveJsonAs = 'schemaOnlyBackupDone'
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'contentMode'; Equals = 'SchemaOnly' }
                @{ Path = 'tables'; Count = 5 }
                @{ Path = 'tables'; ContainsObject = @{ table = 'orders'; dataBackedUp = $false; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'schema-browser-lists-schema-only-backup'
            Type = 'Cli'
            Args = @('schema', 'backups')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ id = '{schemaOnlyBackup.id}'; contentMode = 'SchemaOnly'; tableCount = 5 } }
            )
        }
        @{
            Name = 'schema-browser-show-table'
            Type = 'Cli'
            Args = @('schema', 'show', '--backup-id', '{schemaOnlyBackup.id}', '--database', 'backup_core_source', '--table', 'orders')
            ExpectTextContains = @('backup_core_source', 'orders', 'CREATE TABLE')
        }
        @{
            Name = 'schema-browser-export-database'
            Type = 'Cli'
            Args = @('schema', 'export', '--backup-id', '{schemaOnlyBackup.id}', '--database', 'backup_core_source')
            ExpectTextContains = @('Database: backup_core_source', 'CREATE TABLE backup_core_source.orders')
        }
        @{
            Name = 'start-manual-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreCoreScenarios/selector-all.json')
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
            SaveJsonAs = 'backupDone'
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'startedAt'; NotEmpty = $true }
                @{ Path = 'endedAt'; NotEmpty = $true }
                @{ Path = 'tables'; Count = 5 }
                @{ Path = 'requestedByName'; Equals = 'admin' }
                @{ Path = 'manualRequestJson'; Contains = 'rules' }
                @{ Path = 'tables[1].clickHouseOperationId'; NotEmpty = $true }
                @{ Path = 'tables[1].clickHouseStatus'; Equals = 'BACKUP_CREATED' }
                @{ Path = 'storageRootPath'; Contains = 'backups/runs/manual/' }
                @{ Path = 'tables[1].storagePath'; Contains = '/tables/backup_core_source/line_items/full' }
                @{ Path = 'tables[4].storagePath'; Contains = '/tables/backup_core_source/orders/full' }
                @{ Path = 'tables'; ContainsObject = @{ table = 'log_events'; dataBackedUp = $false; status = 'Succeeded' } }
                @{ Path = 'tables'; ContainsObject = @{ table = 'join_lookup'; dataBackedUp = $false; status = 'Succeeded' } }
                @{ Path = 'tables'; ContainsObject = @{ table = 'merge_orders'; dataBackedUp = $false; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'backup-show'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{backup.id}')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{backup.id}' }
                @{ Path = 'startedAt'; NotEmpty = $true }
                @{ Path = 'endedAt'; NotEmpty = $true }
                @{ Path = 'tables[1].clickHouseOperationId'; NotEmpty = $true }
                @{ Path = 'storageRootPath'; Contains = 'backups/runs/manual/' }
                @{ Path = 'tables[1].storagePath'; Contains = '/tables/backup_core_source/line_items/full' }
            )
        }
        @{
            Name = 'backup-list-by-cluster'
            Type = 'Cli'
            Args = @('backups', 'list', '--cluster-name', 'source')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ id = '{backup.id}'; sourceClusterId = '{sourceCluster.id}' } }
            )
        }
        @{
            Name = 'backup-list-by-table'
            Type = 'Cli'
            Args = @('backups', 'list', '--table-name', 'backup_core_source.orders')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ id = '{backup.id}' } }
            )
        }
        @{
            Name = 'backup-list-by-cluster-and-table'
            Type = 'Cli'
            Args = @('backups', 'list', '--cluster-name', 'source', '--table-name', 'orders')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ id = '{backup.id}' } }
            )
        }
        @{
            Name = 'restore-same-cluster-new-name'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{sourceCluster.id}', '--database', 'backup_core_source', '--table', 'orders', '--target-database', 'backup_core_source', '--target-table', 'orders_same_cluster_copy')
            SaveJsonAs = 'sameClusterRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-same-cluster-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{sameClusterRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'startedAt'; NotEmpty = $true }
                @{ Path = 'endedAt'; NotEmpty = $true }
                @{ Path = 'tables[0].clickHouseOperationId'; NotEmpty = $true }
            )
        }
        @{
            Name = 'restore-show'
            Type = 'Cli'
            Args = @('restores', 'show', '--id', '{sameClusterRestore.id}')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{sameClusterRestore.id}' }
                @{ Path = 'startedAt'; NotEmpty = $true }
                @{ Path = 'endedAt'; NotEmpty = $true }
                @{ Path = 'tables[0].clickHouseStatus'; Equals = 'RESTORED' }
            )
        }
        @{
            Name = 'restore-list'
            Type = 'Cli'
            Args = @('restores', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ id = '{sameClusterRestore.id}'; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'restore-different-cluster-same-table'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_core_source', '--table', 'orders', '--target-database', 'backup_core_restore', '--target-table', 'orders')
            SaveJsonAs = 'differentClusterRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-different-cluster-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{differentClusterRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'restore-full-database'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}')
            SaveJsonAs = 'fullRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-full-database-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{fullRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 5 }
            )
        }
        @{
            Name = 'restore-append'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--table-mappings-json', '[{"backupTableId":"{backupDone.tables.4.id}","targetDatabase":"backup_core_restore","targetTable":"append_orders","schemaOnly":false,"append":true,"allowSchemaMismatch":false}]', '--confirm-destructive')
            SaveJsonAs = 'appendRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 'tables[0].append'; Equals = $true }
                @{ Path = 'tables[0].schemaOnly'; Equals = $false }
            )
        }
        @{
            Name = 'wait-append-matching-schema-restore'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{appendRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
            )
        }
        @{
            Name = 'restore-existing-compatible-without-append-fails'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_core_source', '--table', 'orders', '--target-database', 'backup_core_restore', '--target-table', 'compatible_existing_orders', '--confirm-destructive')
            SaveJsonAs = 'existingNoAppendRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-existing-compatible-without-append-fails'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{existingNoAppendRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'tables[0].error'; Contains = 'already exists' }
            )
        }
        @{
            Name = 'restore-append-missing-table-fails'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_core_source', '--table', 'orders', '--target-database', 'backup_core_restore', '--target-table', 'missing_append_orders', '--append', '--confirm-destructive')
            SaveJsonAs = 'appendMissingRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-append-missing-table-fails'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{appendMissingRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
                @{ Path = 'tables[0].error'; Contains = 'requires target table' }
            )
        }
        @{
            Name = 'restore-mismatch-allowed'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_core_source', '--table', 'orders', '--target-database', 'backup_core_restore', '--target-table', 'mismatch_allowed_orders', '--append', '--allow-schema-mismatch', '--confirm-destructive')
            SaveJsonAs = 'mismatchAllowedRestore'
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-mismatch-allowed'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{mismatchAllowedRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].warning'; Contains = 'schema differs' }
            )
        }
        @{
            Name = 'show-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '200')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'created'; entityType = 'backup' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'succeeded'; entityType = 'backup' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'created'; entityType = 'restore' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'succeeded'; entityType = 'restore' } }
            )
        }
        @{
            Name = 'show-logs'
            Type = 'Cli'
            Args = @('logs', 'show', '--last', '200', '--severity', 'warning,error')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ level = 'Warning' } }
            )
        }
        @{
            Name = 'restore-mismatch-fails'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_core_source', '--table', 'orders', '--target-database', 'backup_core_restore', '--target-table', 'bad_orders', '--append', '--confirm-destructive')
            SaveJsonAs = 'mismatchRestore'
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-mismatch-fails'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{mismatchRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Failed' }
            )
        }
        @{
            Name = 'restore-partial-non-mergetree-schema-only'
            Type = 'Cli'
            Args = @('restore', 'initiate', '--backup-id', '{backup.id}', '--target-cluster-id', '{restoreCluster.id}', '--database', 'backup_core_source', '--table', 'log_events', '--target-database', 'backup_core_restore', '--target-table', 'log_events_only')
            SaveJsonAs = 'partialLogRestore'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-partial-non-mergetree-schema-only'
            Type = 'Cli'
            Args = @('restores', 'wait', '--id', '{partialLogRestore.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables[0].clickHouseStatus'; Equals = 'SCHEMA_ONLY' }
            )
        }
        @{
            Name = 'start-database-selector-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreCoreScenarios/selector-database-only.json')
            SaveJsonAs = 'databaseSelectorBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-database-selector-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{databaseSelectorBackup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 6 }
                @{ Path = 'tables'; ContainsObject = @{ table = 'orders_same_cluster_copy' } }
            )
        }
        @{
            Name = 'start-table-selector-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreCoreScenarios/selector-table-only.json')
            SaveJsonAs = 'tableSelectorBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'wait-table-selector-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{tableSelectorBackup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 1 }
                @{ Path = 'tables'; ContainsObject = @{ database = 'backup_core_source'; table = 'orders' } }
            )
        }
        @{
            Name = 'start-exclude-selector-backup'
            Type = 'Cli'
            Args = @('backup', 'manual', '--cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreCoreScenarios/selector-exclude.json')
            SaveJsonAs = 'excludeSelectorBackup'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'backup-wait-timeout-returns-current-state'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{excludeSelectorBackup.id}', '--timeout-seconds', '0', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{excludeSelectorBackup.id}' }
            )
        }
        @{
            Name = 'wait-exclude-selector-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{excludeSelectorBackup.id}', '--timeout-seconds', '30', '--poll-seconds', '1')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 5 }
                @{ Path = 'tables'; ContainsObject = @{ table = 'orders_same_cluster_copy' } }
            )
        }
        @{
            Name = 'show-failure-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '400')
            RetryTimeoutSeconds = 6
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'table-failed'; entityType = 'restore-table' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'failed'; entityType = 'restore' } }
            )
        }
        @{
            Name = 'add-scheduled-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'scheduled-orders', '--source-cluster-id', '{sourceCluster.id}', '--target-id', '{target.id}', '--selector-file', '/suite/Tests/BackupRestoreCoreScenarios/selector-table-only.json')
            SaveJsonAs = 'scheduledPolicy'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-fast-schedule'
            Type = 'Cli'
            Args = @('schedules', 'add', '--name', 'scheduled-orders', '--policy-id', '{scheduledPolicy.id}', '--backup-type', 'Full', '--cron', '0/1 * * * * ?', '--timezone', 'UTC')
            SaveJsonAs = 'fastSchedule'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 'policyId'; Equals = '{scheduledPolicy.id}' }
            )
        }
        @{
            Name = 'backup-list-by-policy-id'
            Type = 'Cli'
            Args = @('backups', 'list', '--policy-id', '{scheduledPolicy.id}')
            RetryTimeoutSeconds = 12
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ policyId = '{scheduledPolicy.id}'; scheduleId = '{fastSchedule.id}' } }
            )
        }
        @{
            Name = 'backup-list-by-policy-cluster-and-table'
            Type = 'Cli'
            Args = @('backups', 'list', '--policy-id', '{scheduledPolicy.id}', '--cluster-name', 'source', '--table-name', 'orders')
            RetryTimeoutSeconds = 12
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ policyId = '{scheduledPolicy.id}'; sourceClusterId = '{sourceCluster.id}' } }
            )
        }
        @{
            Name = 'scheduled-backup-succeeded'
            Type = 'Cli'
            Args = @('backups', 'list', '--policy-id', '{scheduledPolicy.id}')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ policyId = '{scheduledPolicy.id}'; scheduleId = '{fastSchedule.id}'; status = 'Succeeded' } }
            )
        }
        @{
            Name = 'dashboard-shows-scheduled-policy'
            Type = 'Cli'
            Args = @('dashboard', '--next-hours', '1')
            ExpectTextContains = @(
                'Schedules'
                'Upcoming backups'
                'scheduled-orders'
            )
        }
        @{
            Name = 'metrics-show-policy-freshness'
            Type = 'Cli'
            Args = @('metrics', 'show')
            ExpectTextContains = @(
                'Policies.TimeSecondsSinceLastPolicyBackup.scheduled-orders'
            )
        }
        @{
            Name = 'disable-fast-schedule'
            Type = 'Cli'
            Args = @('schedules', 'disable', '--id', '{fastSchedule.id}')
            ExpectExitCode = 0
        }
    )

    Verify = @(
        @{ Name = 'verify-same-cluster-copy'; Type = 'Csv'; Resource = 'source'; Path = 'Sql/select-same-cluster-copy.sql'; Expected = 'Expected/orders.csv'; Actual = 'same-cluster-copy.csv' }
        @{ Name = 'verify-different-cluster-same-table'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-different-cluster-orders.sql'; Expected = 'Expected/orders.csv'; Actual = 'different-cluster-orders.csv' }
        @{ Name = 'verify-full-database-orders'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-full-database-orders.sql'; Expected = 'Expected/orders.csv'; Actual = 'full-database-orders.csv' }
        @{ Name = 'verify-full-database-line-items'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-full-database-line-items.sql'; Expected = 'Expected/line-items.csv'; Actual = 'full-database-line-items.csv' }
        @{ Name = 'verify-schema-only-log-table'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-log-table-count.sql'; Expected = 'Expected/schema-only-count.csv'; Actual = 'schema-only-count.csv' }
        @{ Name = 'verify-schema-only-engines'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-schema-only-engines.sql'; Expected = 'Expected/schema-only-engines.csv'; Actual = 'schema-only-engines.csv' }
        @{ Name = 'verify-append'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-append-orders.sql'; Expected = 'Expected/append-orders.csv'; Actual = 'append-orders.csv' }
        @{ Name = 'verify-mismatch-allowed'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-mismatch-allowed-orders.sql'; Expected = 'Expected/mismatch-allowed-orders.csv'; Actual = 'mismatch-allowed-orders.csv' }
        @{ Name = 'verify-partial-log-schema-only'; Type = 'Csv'; Resource = 'restore'; Path = 'Sql/select-partial-log-table-count.sql'; Expected = 'Expected/schema-only-count.csv'; Actual = 'partial-log-schema-only-count.csv' }
    )

    Cleanup = @()
}
