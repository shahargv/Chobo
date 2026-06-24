@{
    Name = 'ChoboCrudSmoke'
    Description = 'Exercises ChoboCli auth and CRUD APIs through ChoboServer.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'Cluster'; Shards = 1; Replicas = 2; DnsName = 'source-cluster' }
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
    )

    Action = @(
        @{
            Name = 'add-user'
            Type = 'Cli'
            Args = @('users', 'add', '--username', 'smoke-user')
            SaveJsonAs = 'user'
            ExpectJson = @(
                @{ Path = 'userName'; Equals = 'smoke-user' }
                @{ Path = 'accessToken'; NotEmpty = $true }
            )
        }
        @{
            Name = 'created-user-token-can-call-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', '{user.accessToken}')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'smoke-user'; isActive = $true } }
            )
        }
        @{
            Name = 'list-users'
            Type = 'Cli'
            Args = @('users', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } }
                @{ Path = '$'; ContainsObject = @{ userName = 'smoke-user'; isActive = $true } }
            )
        }
        @{
            Name = 'add-user-token'
            Type = 'Cli'
            Args = @('users', 'add-token', '--id', '{user.userId}', '--name', 'automation')
            SaveJsonAs = 'userToken'
            ExpectJson = @(
                @{ Path = 'userId'; Equals = '{user.userId}' }
                @{ Path = 'accessToken'; NotEmpty = $true }
            )
        }
        @{
            Name = 'list-user-tokens'
            Type = 'Cli'
            Args = @('users', 'tokens', '--id', '{user.userId}')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'automation'; isActive = $true } }
            )
            ExpectTextNotContains = @('{user.accessToken}', '{userToken.accessToken}')
        }
        @{
            Name = 'added-token-can-call-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', '{userToken.accessToken}')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'smoke-user'; isActive = $true } }
            )
        }
        @{
            Name = 'remove-user-token'
            Type = 'Cli'
            Args = @('users', 'remove-token', '--id', '{user.userId}', '--token-id', '{userToken.tokenId}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-user-tokens-after-remove-token'
            Type = 'Cli'
            Args = @('users', 'tokens', '--id', '{user.userId}')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'automation'; isActive = $false } }
            )
            ExpectTextNotContains = @('{user.accessToken}', '{userToken.accessToken}')
        }
        @{
            Name = 'removed-token-cannot-call-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', '{userToken.accessToken}')
            ExpectExitCode = 1
            ExpectTextContains = 'Unauthorized'
        }
        @{
            Name = 'remove-user'
            Type = 'Cli'
            Args = @('users', 'remove', '--id', '{user.userId}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-users-after-remove-user'
            Type = 'Cli'
            Args = @('users', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'smoke-user'; isActive = $false } }
            )
        }
        @{
            Name = 'removed-user-token-cannot-call-api'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', '{user.accessToken}')
            ExpectExitCode = 1
            ExpectTextContains = 'Unauthorized'
        }
        @{
            Name = 'add-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'crud-source-old', '--mode', 'Cluster', '--node', 'source-cluster:9000', '--username', 'default', '--password', 'secret', '--backup-restore-maxdop', '2')
            SaveJsonAs = 'cluster'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'crud-source-old' }
                @{ Path = 'mode'; Equals = 'Cluster' }
                @{ Path = 'accessNodes'; Count = 1 }
            )
            ExpectTextNotContains = @('secret')
        }
        @{
            Name = 'list-clusters'
            Type = 'Cli'
            Args = @('clusters', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'crud-source-old'; mode = 'Cluster' } }
            )
            ExpectTextNotContains = @('secret')
        }
        @{
            Name = 'update-cluster'
            Type = 'Cli'
            Args = @('clusters', 'update', '--id', '{cluster.id}', '--name', 'crud-source-new', '--mode', 'Cluster', '--node', 'source-cluster:9000', '--username', 'default', '--password', 'rotated-secret', '--backup-restore-maxdop', '2', '--clickhouse-cluster-name', '{source.ClusterName}')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{cluster.id}' }
                @{ Path = 'name'; Equals = 'crud-source-new' }
                @{ Path = 'backupRestoreMaxDop'; Equals = '2' }
                @{ Path = 'clickHouseClusterName'; Equals = '{source.ClusterName}' }
                @{ Path = 'accessNodes[0].host'; Equals = 'source-cluster' }
                @{ Path = 'accessNodes[0].port'; Equals = 9000 }
            )
            ExpectTextNotContains = @('rotated-secret')
        }
        @{
            Name = 'list-clusters-after-update'
            Type = 'Cli'
            Args = @('clusters', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'crud-source-new'; mode = 'Cluster' } }
            )
            ExpectTextNotContains = @('crud-source-old', 'rotated-secret')
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'crud-minio-old', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'crud-minio-old' }
                @{ Path = 'type'; Equals = 'S3' }
                @{ Path = 's3.bucket'; Equals = '{backupStore.Bucket}' }
            )
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'list-targets'
            Type = 'Cli'
            Args = @('targets', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'crud-minio-old'; type = 'S3' } }
            )
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'update-s3-target'
            Type = 'Cli'
            Args = @('targets', 'update-s3', '--id', '{target.id}', '--name', 'crud-minio-new', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--region', 'eu-west-1', '--path-prefix', 'crud-prefix', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{target.id}' }
                @{ Path = 'name'; Equals = 'crud-minio-new' }
                @{ Path = 's3.region'; Equals = 'eu-west-1' }
                @{ Path = 's3.pathPrefix'; Equals = 'crud-prefix' }
                @{ Path = 's3.forcePathStyle'; Equals = $false }
            )
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'list-targets-after-update'
            Type = 'Cli'
            Args = @('targets', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'crud-minio-new'; type = 'S3' } }
            )
            ExpectTextNotContains = @('crud-minio-old', '{backupStore.SecretKey}')
        }
        @{
            Name = 'add-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'crud-policy-old', '--source-cluster-id', '{cluster.id}', '--target-id', '{target.id}')
            SaveJsonAs = 'policy'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'crud-policy-old' }
                @{ Path = 'sourceClusterId'; Equals = '{cluster.id}' }
                @{ Path = 'targetId'; Equals = '{target.id}' }
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'update-policy'
            Type = 'Cli'
            Args = @('policies', 'update', '--id', '{policy.id}', '--name', 'crud-policy-new', '--source-cluster-id', '{cluster.id}', '--target-id', '{target.id}', '--full-retention-minutes', '120', '--incremental-retention-minutes', '60', '--min-backups-to-keep', '2', '--min-full-backups-to-keep', '1')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{policy.id}' }
                @{ Path = 'name'; Equals = 'crud-policy-new' }
                @{ Path = 'retention.fullRetentionMinutes'; Equals = 120 }
                @{ Path = 'retention.incrementalRetentionMinutes'; Equals = 60 }
                @{ Path = 'retention.minBackupsToKeep'; Equals = 2 }
                @{ Path = 'retention.minFullBackupsToKeep'; Equals = 1 }
            )
        }
        @{
            Name = 'list-policies-after-update'
            Type = 'Cli'
            Args = @('policies', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'crud-policy-new'; sourceClusterId = '{cluster.id}'; targetId = '{target.id}' } }
            )
            ExpectTextNotContains = @('crud-policy-old')
        }
        @{
            Name = 'evaluate-policy'
            Type = 'Cli'
            Args = @('policies', 'evaluate', '--id', '{policy.id}')
            ExpectJson = @(
                @{ Path = 'policyId'; Equals = '{policy.id}' }
                @{ Path = 'policyName'; Equals = 'crud-policy-new' }
            )
        }
        @{
            Name = 'add-schedule'
            Type = 'Cli'
            Args = @('schedules', 'add', '--name', 'crud-schedule-old', '--policy-id', '{policy.id}', '--backup-type', 'Full', '--cron', '0 0 2 * * ?', '--timezone', 'UTC')
            SaveJsonAs = 'schedule'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'crud-schedule-old' }
                @{ Path = 'policyId'; Equals = '{policy.id}' }
                @{ Path = 'backupType'; Equals = 'Full' }
                @{ Path = 'isEnabled'; Equals = $true }
            )
        }
        @{
            Name = 'update-schedule'
            Type = 'Cli'
            Args = @('schedules', 'update', '--id', '{schedule.id}', '--name', 'crud-schedule-new', '--policy-id', '{policy.id}', '--backup-type', 'Incremental', '--cron', '0 0 */6 * * ?', '--timezone', 'UTC', '--missed-run-grace-period', '00:10:00', '--description', 'updated schedule', '--disabled')
            ExpectJson = @(
                @{ Path = 'id'; Equals = '{schedule.id}' }
                @{ Path = 'name'; Equals = 'crud-schedule-new' }
                @{ Path = 'backupType'; Equals = 'Incremental' }
                @{ Path = 'cronExpression'; Equals = '0 0 */6 * * ?' }
                @{ Path = 'missedRunGracePeriod'; Equals = '00:10:00' }
                @{ Path = 'isEnabled'; Equals = $false }
                @{ Path = 'description'; Equals = 'updated schedule' }
            )
        }
        @{
            Name = 'enable-schedule'
            Type = 'Cli'
            Args = @('schedules', 'enable', '--id', '{schedule.id}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-schedules-after-enable'
            Type = 'Cli'
            Args = @('schedules', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'crud-schedule-new'; isEnabled = $true } }
            )
            ExpectTextNotContains = @('crud-schedule-old')
        }
        @{
            Name = 'disable-schedule'
            Type = 'Cli'
            Args = @('schedules', 'disable', '--id', '{schedule.id}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-schedules'
            Type = 'Cli'
            Args = @('schedules', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'crud-schedule-new'; isEnabled = $false } }
            )
        }
        @{
            Name = 'remove-schedule'
            Type = 'Cli'
            Args = @('schedules', 'remove', '--id', '{schedule.id}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-schedules-after-remove'
            Type = 'Cli'
            Args = @('schedules', 'list')
            ExpectTextNotContains = @('crud-schedule-new')
        }
        @{
            Name = 'removed-schedule-cannot-be-updated'
            Type = 'Cli'
            Args = @('schedules', 'update', '--id', '{schedule.id}', '--name', 'removed-schedule', '--policy-id', '{policy.id}', '--backup-type', 'Full', '--cron', '0 0 3 * * ?', '--timezone', 'UTC')
            ExpectExitCode = 1
            ExpectTextContains = 'Not Found'
        }
        @{
            Name = 'remove-policy'
            Type = 'Cli'
            Args = @('policies', 'remove', '--id', '{policy.id}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-policies-after-remove'
            Type = 'Cli'
            Args = @('policies', 'list')
            ExpectTextNotContains = @('crud-policy-new')
        }
        @{
            Name = 'removed-policy-cannot-be-evaluated'
            Type = 'Cli'
            Args = @('policies', 'evaluate', '--id', '{policy.id}')
            ExpectExitCode = 1
            ExpectTextContains = 'Not Found'
        }
        @{
            Name = 'remove-target'
            Type = 'Cli'
            Args = @('targets', 'remove', '--id', '{target.id}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-targets-after-remove'
            Type = 'Cli'
            Args = @('targets', 'list')
            ExpectTextNotContains = @('crud-minio-new', '{backupStore.SecretKey}')
        }
        @{
            Name = 'removed-target-cannot-be-updated'
            Type = 'Cli'
            Args = @('targets', 'update-s3', '--id', '{target.id}', '--name', 'removed-target', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}')
            ExpectExitCode = 1
            ExpectTextContains = 'Not Found'
        }
        @{
            Name = 'remove-cluster'
            Type = 'Cli'
            Args = @('clusters', 'remove', '--id', '{cluster.id}')
            ExpectExitCode = 0
        }
        @{
            Name = 'list-clusters-after-remove'
            Type = 'Cli'
            Args = @('clusters', 'list')
            ExpectTextNotContains = @('crud-source-new', 'rotated-secret')
        }
        @{
            Name = 'removed-cluster-cannot-be-updated'
            Type = 'Cli'
            Args = @('clusters', 'update', '--id', '{cluster.id}', '--name', 'removed-cluster', '--mode', 'SingleInstance', '--host', '{source.Host}', '--backup-restore-maxdop', '2')
            ExpectExitCode = 1
            ExpectTextContains = 'Not Found'
        }
        @{
            Name = 'show-logs'
            Type = 'Cli'
            Args = @('logs', 'show', '--last', '20')
            ExpectExitCode = 0
        }
        @{
            Name = 'show-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '50')
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'create'; entityType = 'backup-schedule' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'update'; entityType = 'backup-schedule' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'delete'; entityType = 'backup-schedule' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'delete'; entityType = 'backup-target' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'delete'; entityType = 'cluster' } }
            )
        }
    )

    Verify = @(
        @{
            Name = 'verify-clusters'
            Type = 'Cli'
            Args = @('clusters', 'list')
            ExpectTextNotContains = @('crud-source-new')
        }
        @{
            Name = 'verify-targets'
            Type = 'Cli'
            Args = @('targets', 'list')
            ExpectTextNotContains = @('crud-minio-new', '{backupStore.SecretKey}')
        }
        @{
            Name = 'verify-policies'
            Type = 'Cli'
            Args = @('policies', 'list')
            ExpectTextNotContains = @('crud-policy-new')
        }
        @{
            Name = 'verify-schedules'
            Type = 'Cli'
            Args = @('schedules', 'list')
            ExpectTextNotContains = @('crud-schedule-new')
        }
        @{
            Name = 'verify-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '50')
            ExpectJson = @(
                @{ Path = 'items'; ContainsObject = @{ action = 'delete'; entityType = 'backup-schedule' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'delete'; entityType = 'backup-policy' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'delete'; entityType = 'backup-target' } }
                @{ Path = 'items'; ContainsObject = @{ action = 'delete'; entityType = 'cluster' } }
            )
        }
    )

    Cleanup = @()
}
