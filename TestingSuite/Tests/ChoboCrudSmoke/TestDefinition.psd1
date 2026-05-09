@{
    Name = 'ChoboCrudSmoke'
    Description = 'Exercises ChoboCli auth and first CRUD APIs through ChoboServer.'
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
            Name = 'list-users'
            Type = 'Cli'
            Args = @('users', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } }
                @{ Path = '$'; ContainsObject = @{ userName = 'smoke-user'; isActive = $true } }
            )
        }
        @{
            Name = 'add-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'Cluster', '--node', 'source-cluster:9000', '--username', 'default', '--password', 'secret')
            SaveJsonAs = 'cluster'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'source' }
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
                @{ Path = '$'; ContainsObject = @{ name = 'source'; mode = 'Cluster' } }
            )
            ExpectTextNotContains = @('secret')
        }
        @{
            Name = 'add-s3-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'minio' }
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
                @{ Path = '$'; ContainsObject = @{ name = 'minio'; type = 'S3' } }
            )
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'add-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'all', '--source-cluster-id', '{cluster.id}', '--target-id', '{target.id}')
            SaveJsonAs = 'policy'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'all' }
                @{ Path = 'sourceClusterId'; Equals = '{cluster.id}' }
                @{ Path = 'targetId'; Equals = '{target.id}' }
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'evaluate-policy'
            Type = 'Cli'
            Args = @('policies', 'evaluate', '--id', '{policy.id}')
            ExpectJson = @(
                @{ Path = 'policyId'; Equals = '{policy.id}' }
                @{ Path = 'policyName'; Equals = 'all' }
            )
        }
        @{
            Name = 'add-schedule'
            Type = 'Cli'
            Args = @('schedules', 'add', '--name', 'nightly', '--policy-id', '{policy.id}', '--backup-type', 'Full', '--cron', '0 0 2 * * ?', '--timezone', 'UTC')
            SaveJsonAs = 'schedule'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'nightly' }
                @{ Path = 'policyId'; Equals = '{policy.id}' }
                @{ Path = 'backupType'; Equals = 'Full' }
                @{ Path = 'isEnabled'; Equals = $true }
            )
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
                @{ Path = '$'; ContainsObject = @{ name = 'nightly'; isEnabled = $false } }
            )
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
                @{ Path = '$'; ContainsObject = @{ action = 'create'; entityType = 'backup-schedule' } }
            )
        }
    )

    Verify = @(
        @{
            Name = 'verify-clusters'
            Type = 'Cli'
            Args = @('clusters', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'source'; mode = 'Cluster' } }
            )
        }
        @{
            Name = 'verify-targets'
            Type = 'Cli'
            Args = @('targets', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ name = 'minio'; type = 'S3' } }
            )
        }
        @{
            Name = 'verify-audit'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '50')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ action = 'create'; entityType = 'backup-schedule' } }
            )
        }
    )

    Cleanup = @()
}
