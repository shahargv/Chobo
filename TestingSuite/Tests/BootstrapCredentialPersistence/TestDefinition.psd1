@{
    Name = 'BootstrapCredentialPersistence'
    Description = 'Verifies automatic bootstrap, keyed credential persistence, and storage/cluster connection tests across server restart.'
    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
        @{ Name = 'source'; Type = 'SingleNode' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )

    Setup = @()

    Action = @(
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
            Name = 'add-target'
            Type = 'Cli'
            Args = @('targets', 'add-s3', '--name', 'minio', '--endpoint', '{backupStore.Endpoint}', '--bucket', '{backupStore.Bucket}', '--access-key', '{backupStore.AccessKey}', '--secret-key', '{backupStore.SecretKey}', '--force-path-style')
            SaveJsonAs = 'target'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 's3.bucket'; Equals = '{backupStore.Bucket}' }
            )
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'add-cluster'
            Type = 'Cli'
            Args = @('clusters', 'add', '--name', 'source', '--mode', 'SingleInstance', '--node', 'clickhouse-source:9000', '--username', 'default')
            SaveJsonAs = 'cluster'
            ExpectJson = @(
                @{ Path = 'id'; NotEmpty = $true }
                @{ Path = 'name'; Equals = 'source' }
            )
            ExpectTextNotContains = @('{backupStore.SecretKey}')
        }
        @{
            Name = 'test-target-before-restart'
            Type = 'Cli'
            Args = @('targets', 'test-connection', '--id', '{target.id}')
            ExpectJson = @(
                @{ Path = 'targetId'; Equals = '{target.id}' }
                @{ Path = 'targetType'; Equals = 'S3' }
                @{ Path = 'succeeded'; Equals = $true }
            )
        }
        @{
            Name = 'test-cluster-before-restart'
            Type = 'Cli'
            Args = @('clusters', 'test-connection', '--id', '{cluster.id}')
            ExpectJson = @(
                @{ Path = 'clusterId'; Equals = '{cluster.id}' }
                @{ Path = 'succeeded'; Equals = $true }
            )
        }
        @{
            Name = 'crash-server'
            Type = 'Cli'
            Args = @('test-hooks', 'crash')
            ExpectJson = @(
                @{ Path = 'crashing'; Equals = $true }
            )
        }
        @{
            Name = 'wait-server-after-restart'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } }
            )
        }
        @{
            Name = 'test-target-after-restart'
            Type = 'Cli'
            Args = @('targets', 'test-connection', '--id', '{target.id}')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = 'targetId'; Equals = '{target.id}' }
                @{ Path = 'succeeded'; Equals = $true }
            )
        }
        @{
            Name = 'test-cluster-after-restart'
            Type = 'Cli'
            Args = @('clusters', 'test-connection', '--id', '{cluster.id}')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 2
            ExpectJson = @(
                @{ Path = 'clusterId'; Equals = '{cluster.id}' }
                @{ Path = 'succeeded'; Equals = $true }
            )
        }
    )

    Verify = @()
    Cleanup = @()
}
