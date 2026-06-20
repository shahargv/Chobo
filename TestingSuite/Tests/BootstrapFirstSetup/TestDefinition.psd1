@{
    Name = 'BootstrapFirstSetup'
    Description = 'Verifies production first setup starts in initialization mode, serves GUI onboarding, finalizes install once, and closes anonymous token creation.'
    Resources = @(
        @{
            Name = 'server'
            Type = 'ChoboServer'
            Environment = @{
                CHOBO_INIT_ADMIN_USER = ''
                CHOBO_INIT_ACCESS_TOKEN = ''
            }
        }
    )

    Setup = @()

    Action = @(
        @{
            Name = 'wait-install-status'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', '$status = Invoke-RestMethod -Uri http://choboserver:8080/api/v1/server/install/status; if (-not $status.requiresInstallation) { throw "Server was not in initialization mode." }; $status.message')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectTextContains = 'waiting for first-time installation'
        }
        @{
            Name = 'gui-serves-install-experience'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', '$index = Invoke-WebRequest -UseBasicParsing -Uri http://choboserver:8080/; $asset = [regex]::Match($index.Content, "assets/[^""'']+\.js").Value; if (-not $asset) { throw "Could not find GUI JavaScript bundle." }; $js = (Invoke-WebRequest -UseBasicParsing -Uri "http://choboserver:8080/$asset").Content; if ($js -notmatch "Install Chobo" -or $js -notmatch "Ready to start") { throw "GUI install screen was not present in the served bundle." }; "GUI install screen present"')
            RetryTimeoutSeconds = 90
            RetryIntervalSeconds = 2
            ExpectTextContains = 'GUI install screen present'
        }
        @{
            Name = 'install-from-cli'
            Type = 'Cli'
            Args = @('server', 'install', '--server-url', 'http://choboserver:8080')
            SaveJsonAs = 'install'
            ExpectJson = @(
                @{ Path = 'userName'; Equals = 'admin' }
                @{ Path = 'accessToken'; NotEmpty = $true }
            )
        }
        @{
            Name = 'profile-authenticated-after-install'
            Type = 'Cli'
            Args = @('users', 'list')
            ExpectJson = @(
                @{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } }
            )
        }
        @{
            Name = 'install-cannot-run-twice'
            Type = 'Cli'
            Args = @('server', 'install', '--server-url', 'http://choboserver:8080')
            ExpectExitCode = 1
            ExpectTextContains = 'already been finalized'
            ExpectTextNotContains = @('{install.accessToken}')
        }
        @{
            Name = 'anonymous-users-still-rejected'
            Type = 'Shell'
            Args = @('pwsh', '-NoProfile', '-Command', 'try { Invoke-WebRequest -UseBasicParsing -Uri http://choboserver:8080/api/v1/users -ErrorAction Stop | Out-Null; throw "Anonymous users request succeeded." } catch { if ($_.Exception.Response.StatusCode.value__ -ne 401) { throw } }; "anonymous rejected"')
            ExpectTextContains = 'anonymous rejected'
        }
    )

    Verify = @()
    Cleanup = @()
}

