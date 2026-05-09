function Get-ChoboClickHouseEndpoint {
    param(
        [Parameter(Mandatory)] $Context,
        [string]$ResourceName,
        [string]$HostOverride
    )

    $name = if ($ResourceName) { $ResourceName } else { $Context.DefaultResourceName }
    $resource = $Context.Resources.$name
    if (-not $resource) {
        throw "Unknown ClickHouse resource '$name'."
    }

    [pscustomobject]@{
        Host = if ($HostOverride) { $HostOverride } else { $resource.Host }
        Port = if ($resource.Port) { [int]$resource.Port } else { 9000 }
    }
}

function Invoke-ChoboClickHouseQuery {
    param(
        $Context,
        [Parameter(Mandatory)] [string]$Query,
        [string]$OutputPath,
        [string]$HostOverride,
        [string]$ResourceName
    )

    if (-not $Context) {
        throw 'Invoke-ChoboClickHouseQuery requires Context.'
    }

    $endpoint = Get-ChoboClickHouseEndpoint -Context $Context -ResourceName $ResourceName -HostOverride $HostOverride
    $args = @('--host', $endpoint.Host, '--port', "$($endpoint.Port)", '--multiquery', '--query', $Query)
    $output = & clickhouse-client @args 2>&1
    $exitCode = $LASTEXITCODE

    if ($OutputPath) {
        Set-Content -Path $OutputPath -Value ($output -join [Environment]::NewLine) -NoNewline
    }

    if ($exitCode -ne 0) {
        throw "clickhouse-client failed with exit code $exitCode on $($endpoint.Host): $($output -join [Environment]::NewLine)"
    }

    $output
}

function Wait-ChoboClickHouse {
    param(
        [Parameter(Mandatory)] $Context,
        [int]$TimeoutSeconds = 120
    )

    foreach ($resourceName in $Context.Resources.PSObject.Properties.Name) {
        $resource = $Context.Resources.$resourceName
        if ($resource.Type -eq 'S3') {
            continue
        }
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $ready = $false

        do {
            try {
                Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -Query 'SELECT 1' | Out-Null
                if ($resource.Type -eq 'Cluster') {
                    Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -Query "SELECT count() FROM system.zookeeper WHERE path = '/'" | Out-Null
                }
                $ready = $true
                break
            } catch {
                Start-Sleep -Seconds 2
            }
        } while ((Get-Date) -lt $deadline)

        if (-not $ready) {
            throw "ClickHouse resource '$resourceName' did not become ready within $TimeoutSeconds seconds."
        }
    }
}

function Register-ChoboResourceDnsAliases {
    param(
        [Parameter(Mandatory)] $Context
    )

    foreach ($resourceName in $Context.Resources.PSObject.Properties.Name) {
        $resource = $Context.Resources.$resourceName
        if ([string]::IsNullOrWhiteSpace($resource.DnsName) -or $resource.DnsName -eq $resource.Host) {
            continue
        }

        $hostEntry = & getent hosts $resource.Host 2>$null | Select-Object -First 1
        if (-not $hostEntry) {
            continue
        }

        $ip = ($hostEntry -split '\s+')[0]
        $existing = Select-String -Path /etc/hosts -Pattern "(\s|^)$([regex]::Escape($resource.DnsName))(\s|$)" -Quiet
        if (-not $existing) {
            Add-Content -Path /etc/hosts -Value "$ip $($resource.DnsName)"
        }
    }
}

Export-ModuleMember -Function Invoke-ChoboClickHouseQuery, Wait-ChoboClickHouse, Get-ChoboClickHouseEndpoint, Register-ChoboResourceDnsAliases
