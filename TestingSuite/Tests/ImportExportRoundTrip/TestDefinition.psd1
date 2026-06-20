@{
    Name = 'ImportExportRoundTrip'
    Description = 'Verifies full data export/import round-trips operational metadata while excluding audit and logs.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
    )
    TimeoutSeconds = 180

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
    )

    Action = @(
        @{
            Name = 'seed-export-import-graph'
            Type = 'Cli'
            Args = @('test-hooks', 'seed-export-import-graph')
            SaveJsonAs = 'seed'
            ExpectJson = @(
                @{ Path = 'clusterId'; NotEmpty = $true }
                @{ Path = 'targetId'; NotEmpty = $true }
                @{ Path = 'backupId'; NotEmpty = $true }
                @{ Path = 'restoreId'; NotEmpty = $true }
            )
        }
        @{
            Name = 'export-data'
            Type = 'Cli'
            Args = @('data', 'export')
            ExpectJson = @(
                @{ Path = 'data.clusters'; ContainsObject = @{ id = '{seed.clusterId}'; clickHouseClusterName = 'system_export_cluster_name' } }
            )
        }
        @{
            Name = 'inspect-export-file'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', '$path=''{TestOutput}/export-data.out.txt''; $raw=Get-Content -LiteralPath $path -Raw; if ($raw.Contains(''source-export-audit-marker'')) { throw ''audit marker was exported'' }; if ($raw.Contains(''source export log marker'')) { throw ''log marker was exported'' }; $json=$raw | ConvertFrom-Json; foreach ($name in ''users'',''accessTokens'',''clusters'',''backupTargets'',''backupPolicies'',''backupSchedules'',''schemaDefinitions'',''backups'',''backupTables'',''backupTableShards'',''restores'',''restoreTables'',''restoreTableShards'') { if (@($json.data.$name).Count -lt 1) { throw (''missing '' + $name) } }; $cluster=$json.data.clusters | Where-Object { $_.id -eq ''{seed.clusterId}'' } | Select-Object -First 1; if (-not $cluster -or $cluster.clickHouseClusterName -ne ''system_export_cluster_name'') { throw ''missing cluster export name'' }; ''export-ok''')
            ExpectTextContains = 'export-ok'
        }
        @{
            Name = 'import-data'
            Type = 'Cli'
            Args = @('data', 'import', '--file', '{TestOutput}/export-data.out.txt')
            ExpectExitCode = 0
        }
    )

    Verify = @(
        @{
            Name = 'verify-cluster'
            Type = 'Cli'
            Args = @('clusters', 'list')
            ExpectJson = @(@{ Path = '$'; ContainsObject = @{ id = '{seed.clusterId}'; name = 'system-export-cluster'; clickHouseClusterName = 'system_export_cluster_name' } })
        }
        @{
            Name = 'verify-target'
            Type = 'Cli'
            Args = @('targets', 'list')
            ExpectJson = @(@{ Path = '$'; ContainsObject = @{ id = '{seed.targetId}'; name = 'system-export-target' } })
        }
        @{
            Name = 'verify-policy'
            Type = 'Cli'
            Args = @('policies', 'list')
            ExpectJson = @(@{ Path = '$'; ContainsObject = @{ id = '{seed.policyId}'; name = 'system-export-policy'; sourceClusterId = '{seed.clusterId}'; targetId = '{seed.targetId}' } })
        }
        @{
            Name = 'verify-schedule'
            Type = 'Cli'
            Args = @('schedules', 'list')
            ExpectJson = @(@{ Path = '$'; ContainsObject = @{ id = '{seed.scheduleId}'; name = 'system-export-schedule'; policyId = '{seed.policyId}' } })
        }
        @{
            Name = 'verify-backup'
            Type = 'Cli'
            Args = @('backups', 'show', '--id', '{seed.backupId}')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 1 }
                @{ Path = 'tables'; ContainsObject = @{ id = '{seed.backupTableId}'; schemaDefinitionId = '{seed.schemaDefinitionId}' } }
            )
        }
        @{
            Name = 'verify-restore'
            Type = 'Cli'
            Args = @('restores', 'show', '--id', '{seed.restoreId}')
            ExpectJson = @(
                @{ Path = 'status'; Equals = 'Succeeded' }
                @{ Path = 'tables'; Count = 1 }
                @{ Path = 'tables'; ContainsObject = @{ id = '{seed.restoreTableId}'; backupTableId = '{seed.backupTableId}' } }
            )
        }
        @{
            Name = 'verify-import-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '20')
            ExpectJson = @(@{ Path = 'items'; ContainsObject = @{ action = 'import'; entityType = 'data' } })
            ExpectTextContains = @('credentialsImportedAsEmpty', 'importedBackups', 'importedRestores')
        }
    )

    Cleanup = @()
}