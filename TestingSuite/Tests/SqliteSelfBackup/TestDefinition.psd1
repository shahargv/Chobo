@{
    Name = 'SqliteSelfBackup'
    Description = 'Verifies ChoboServer creates local SQLite self-backups and records logs plus audit entries.'
    Resources = @(
        @{
            Name = 'server'
            Type = 'ChoboServer'
            Environment = @{
                CHOBO_SQLITE_SELF_BACKUP_ENABLED = 'true'
                CHOBO_SQLITE_SELF_BACKUP_INTERVAL = '00:00:01'
                CHOBO_SQLITE_SELF_BACKUP_POLL_INTERVAL = '00:00:01'
            }
        }
    )
    TimeoutSeconds = 120

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
    )

    Action = @()

    Verify = @(
        @{
            Name = 'self-backup-audit-created'
            Type = 'Cli'
            Args = @('audit', 'show', '--last', '20', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 1
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ action = 'sqlite-self-backup-created'; entityType = 'sqlite-self-backup'; actorName = 'system' } }
            )
        }
        @{
            Name = 'self-backup-log-created'
            Type = 'Cli'
            Args = @('logs', 'show', '--last', '50', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
            RetryTimeoutSeconds = 30
            RetryIntervalSeconds = 1
            ExpectTextContains = 'Created SQLite self-backup'
        }
    )

    Cleanup = @()
}
