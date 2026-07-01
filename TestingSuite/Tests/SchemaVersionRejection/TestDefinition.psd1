@{
    Name = 'SchemaVersionRejection'
    Description = 'Mutates SQLite schema version above the supported server version, restarts, and verifies startup refuses the database.'

    Resources = @(
        @{ Name = 'server'; Type = 'ChoboServer' }
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
    )

    Action = @(
        @{
            Name = 'set-future-schema-version-and-crash'
            Type = 'Cli'
            Args = @('test-hooks', 'set-future-schema-version-and-crash')
            ExpectJson = @(
                @{ Path = 'schemaVersion'; NotEmpty = $true }
                @{ Path = 'crashing'; Equals = $true }
            )
        }
    )

    Verify = @(
        @{
            Name = 'server-refuses-future-schema'
            Type = 'Cli'
            Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 2
            ExpectExitCode = 1
        }
    )

    Cleanup = @()
}
