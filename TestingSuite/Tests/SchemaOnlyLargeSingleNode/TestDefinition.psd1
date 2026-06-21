@{
    Name = 'SchemaOnlyLargeSingleNode'
    Description = 'Runs a schema-only backup for 1,000 tables on a single-node ClickHouse source and verifies summary behavior.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'SingleNode' }
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
            Name = 'auth-profile'
            Type = 'Cli'
            Args = @('server', 'auth', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            ExpectTextContains = 'Authenticated to http://choboserver:8080.'
        }
        @{ Name = 'create-1000-source-tables'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-1000-source-tables.sql' }
    )

    Action = @(
        @{
            Name = 'add-source-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'large-source', '--mode', 'SingleInstance', '--host', '{source.Host}', '--backup-restore-maxdop', '1')
            SaveJsonAs = 'sourceCluster'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'large-source' }
                @{ Path = 'mode'; Equals = 'SingleInstance' }
            )
        }
        @{
            Name = 'add-schema-only-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'large-schema-only', '--source-cluster-id', '{sourceCluster.id}', '--schema-only')
            SaveJsonAs = 'schemaOnlyPolicy'
            ExpectJson = @(
                @{ Path = 'contentMode'; Equals = 'SchemaOnly' }
                @{ Path = 'isSystemDefault'; Equals = $false }
            )
        }
        @{
            Name = 'start-schema-only-backup'
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
            Name = 'wait-schema-only-backup'
            Type = 'Cli'
            Args = @('backups', 'wait', '--id', '{schemaOnlyBackup.id}', '--timeout-seconds', '45', '--poll-seconds', '1')
            SaveJsonAs = 'schemaOnlyBackupDone'
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'contentMode'; Equals = 'SchemaOnly' }
                @{ Path = 'tableCount'; Equals = 1000 }
                @{ Path = 'tables'; Count = 1000 }
                @{ Path = 'tables'; ContainsObject = @{ database = 'large_schema'; table = 'table_0999'; dataBackedUp = $false; status = 'Succeeded' } }
            )
        }
    )

    Verify = @(
        @{
            Name = 'schema-browser-lists-large-schema-only-backup'
            Type = 'Cli'
            Args = @('schema', 'backups')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ id = '{schemaOnlyBackup.id}'; contentMode = 'SchemaOnly'; tableCount = 1000 } }
            )
        }
        @{
            Name = 'schema-browser-show-last-table'
            Type = 'Cli'
            Args = @('schema', 'show', '--backup-id', '{schemaOnlyBackup.id}', '--database', 'large_schema', '--table', 'table_0999')
            ExpectTextContains = @('large_schema', 'table_0999', 'CREATE TABLE')
        }
        @{
            Name = 'audit-has-aggregate-schema-only-skip'
            Type = 'Cli'
            Args = @('audit', 'show', '--operation-id', '{schemaOnlyBackup.id}', '--limit', '20')
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'schema-only-tables-skipped'; entityType = 'backup'; entityId = '{schemaOnlyBackup.id}' } }
            )
            ExpectTextNotContains = 'table-skipped'
        }
    )

    Cleanup = @()
}
