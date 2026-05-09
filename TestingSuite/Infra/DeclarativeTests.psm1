function Get-ChoboDeclarativeTokens {
    param(
        [Parameter(Mandatory)] $Context
    )

    $tokens = @{
        RunId = $Context.RunId
        TestName = $Context.TestName
        TestId = $Context.TestId
        DatabaseName = $Context.DatabaseName
        TableName = $Context.TableName
    }

    foreach ($resourceName in $Context.Resources.PSObject.Properties.Name) {
        $resource = $Context.Resources.$resourceName
        $tokens["$resourceName.Name"] = $resource.Name
        $tokens["$resourceName.Type"] = $resource.Type
        $tokens["$resourceName.Shards"] = $resource.Shards
        $tokens["$resourceName.Replicas"] = $resource.Replicas
        $tokens["$resourceName.Host"] = $resource.Host
        $tokens["$resourceName.DnsName"] = $resource.DnsName
        $tokens["$resourceName.DatabaseName"] = $resource.DatabaseName
        $tokens["$resourceName.TableName"] = $resource.TableName
        $tokens["$resourceName.ClusterName"] = $resource.ClusterName
        $tokens["$resourceName.ReplicaHost"] = $resource.ReplicaHost
        $tokens["$resourceName.Endpoint"] = $resource.Endpoint
        $tokens["$resourceName.Bucket"] = $resource.Bucket
        $tokens["$resourceName.AccessKey"] = $resource.AccessKey
        $tokens["$resourceName.SecretKey"] = $resource.SecretKey
    }

    $tokens
}

function Expand-ChoboTemplate {
    param(
        [Parameter(Mandatory)] [string]$Text,
        [Parameter(Mandatory)] [hashtable]$Tokens
    )

    $expanded = $Text
    foreach ($entry in $Tokens.GetEnumerator()) {
        $expanded = $expanded.Replace("{$($entry.Key)}", [string]$entry.Value)
    }

    $expanded
}

function Get-ChoboStepText {
    param(
        [Parameter(Mandatory)] $Step,
        [Parameter(Mandatory)] [string]$TestRoot,
        [Parameter(Mandatory)] [hashtable]$Tokens
    )

    if ($Step.ContainsKey('Query')) {
        return Expand-ChoboTemplate -Text $Step.Query -Tokens $Tokens
    }

    if ($Step.ContainsKey('Path')) {
        $path = Join-Path $TestRoot $Step.Path
        $text = Get-Content -Path $path -Raw
        return Expand-ChoboTemplate -Text $text -Tokens $Tokens
    }

    throw 'Declarative step must provide Query or Path.'
}

function Invoke-ChoboDeclarativeSteps {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string]$TestRoot,
        [object[]]$Steps = @()
    )

    $tokens = Get-ChoboDeclarativeTokens -Context $Context
    $index = 0

    foreach ($step in @($Steps)) {
        $index++
        $type = if ($step.ContainsKey('Type')) { $step.Type } else { 'Sql' }
        $name = if ($step.ContainsKey('Name')) { $step.Name } else { '{0:D2}-{1}' -f $index, $type.ToLowerInvariant() }

        switch ($type) {
            'Sql' {
                $query = Get-ChoboStepText -Step $step -TestRoot $TestRoot -Tokens $tokens
                if ([string]::IsNullOrWhiteSpace($query)) {
                    continue
                }

                $queryPath = Join-Path $Context.OutputDirectory "$name.sql"
                Set-Content -Path $queryPath -Value $query
                $hostOverride = if ($step.ContainsKey('Host')) { Expand-ChoboTemplate -Text $step.Host -Tokens $tokens } else { $null }
                $resourceName = if ($step.ContainsKey('Resource')) { Expand-ChoboTemplate -Text $step.Resource -Tokens $tokens } else { $Context.DefaultResourceName }
                Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -HostOverride $hostOverride -Query $query | Out-Null
            }
            'Csv' {
                $query = Get-ChoboStepText -Step $step -TestRoot $TestRoot -Tokens $tokens
                $queryPath = Join-Path $Context.OutputDirectory "$name.sql"
                Set-Content -Path $queryPath -Value $query

                $actualName = if ($step.ContainsKey('Actual')) { Expand-ChoboTemplate -Text $step.Actual -Tokens $tokens } else { "$name.csv" }
                $actualPath = Join-Path $Context.OutputDirectory $actualName
                $expectedRelative = Expand-ChoboTemplate -Text $step.Expected -Tokens $tokens
                $expectedPath = Join-Path $TestRoot $expectedRelative
                $hostOverride = if ($step.ContainsKey('Host')) { Expand-ChoboTemplate -Text $step.Host -Tokens $tokens } else { $null }
                $resourceName = if ($step.ContainsKey('Resource')) { Expand-ChoboTemplate -Text $step.Resource -Tokens $tokens } else { $Context.DefaultResourceName }

                Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -HostOverride $hostOverride -Query $query -OutputPath $actualPath | Out-Null
                Assert-ChoboCsvEquals -ExpectedPath $expectedPath -ActualPath $actualPath
            }
            'Cli' {
                $command = Get-ChoboStepText -Step $step -TestRoot $TestRoot -Tokens $tokens
                $commandPath = Join-Path $Context.OutputDirectory "$name.cli.txt"
                Set-Content -Path $commandPath -Value $command
                $arguments = [System.Management.Automation.PSParser]::Tokenize($command, [ref]$null) |
                    Where-Object { $_.Type -eq 'CommandArgument' -or $_.Type -eq 'Command' } |
                    ForEach-Object { $_.Content }
                Invoke-ChoboCli @arguments
            }
            default {
                throw "Unsupported declarative step type '$type'."
            }
        }
    }
}

function Invoke-ChoboDefaultDatabaseSetup {
    param(
        [Parameter(Mandatory)] $Context
    )

    foreach ($resourceName in $Context.Resources.PSObject.Properties.Name) {
        $resource = $Context.Resources.$resourceName
        if ($resource.Type -eq 'S3') {
            continue
        }
        $database = $resource.DatabaseName
        $query = if ($resource.Type -eq 'Cluster') {
            "CREATE DATABASE IF NOT EXISTS $database ON CLUSTER $($resource.ClusterName) ENGINE = Replicated('/clickhouse/databases/$database', '{shard}', '{replica}');"
        } else {
            "CREATE DATABASE IF NOT EXISTS $database ENGINE = Atomic;"
        }

        Set-Content -Path (Join-Path $Context.OutputDirectory "infra-create-database-$resourceName.sql") -Value $query
        Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -Query $query | Out-Null
    }
}

function Invoke-ChoboDefaultReplicaSync {
    param(
        [Parameter(Mandatory)] $Context
    )

    foreach ($resourceName in $Context.Resources.PSObject.Properties.Name) {
        $resource = $Context.Resources.$resourceName
        if ($resource.Type -ne 'Cluster') {
            continue
        }

        $existsOutput = Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -Query "EXISTS TABLE $($resource.DatabaseName).$($resource.TableName)"
        if (($existsOutput -join '').Trim() -ne '1') {
            continue
        }

        $query = "SYSTEM SYNC REPLICA $($resource.DatabaseName).$($resource.TableName);"
        Set-Content -Path (Join-Path $Context.OutputDirectory "infra-sync-replica-$resourceName.sql") -Value $query
        Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -HostOverride $resource.ReplicaHost -Query $query | Out-Null
    }
}

function Invoke-ChoboDefaultCleanup {
    param(
        [Parameter(Mandatory)] $Context
    )

    foreach ($resourceName in $Context.Resources.PSObject.Properties.Name) {
        $resource = $Context.Resources.$resourceName
        if ($resource.Type -eq 'S3') {
            continue
        }
        $clusterClause = if ($resource.Type -eq 'Cluster') { " ON CLUSTER $($resource.ClusterName)" } else { '' }
        $query = "DROP DATABASE IF EXISTS $($resource.DatabaseName)$clusterClause SYNC;"
        Set-Content -Path (Join-Path $Context.OutputDirectory "infra-drop-database-$resourceName.sql") -Value $query
        Invoke-ChoboClickHouseQuery -Context $Context -ResourceName $resourceName -Query $query | Out-Null
    }
}

function New-ChoboDeclarativeTestDefinition {
    param(
        [Parameter(Mandatory)] [string]$DefinitionPath
    )

    $definition = Import-PowerShellDataFile -Path $DefinitionPath
    $testRoot = Split-Path -Parent $DefinitionPath

    [pscustomobject]@{
        Name = $definition.Name
        Description = $definition.Description
        TimeoutSeconds = if ($definition.ContainsKey('TimeoutSeconds')) { $definition.TimeoutSeconds } else { $null }
        EnvironmentReuseGroup = if ($definition.ContainsKey('EnvironmentReuseGroup')) { $definition.EnvironmentReuseGroup } else { 'clickhouse' }
        ExcludeFromRunAll = if ($definition.ContainsKey('ExcludeFromRunAll')) { [bool]$definition.ExcludeFromRunAll } else { $false }
        Resources = if ($definition.ContainsKey('Resources')) { @($definition.Resources) } else { @(@{ Name = 'source'; Type = 'SingleNode' }) }
        Path = $DefinitionPath
        Kind = 'Declarative'
        TestRoot = $testRoot
        UseDefaultDatabaseSetup = if ($definition.ContainsKey('UseDefaultDatabaseSetup')) { [bool]$definition.UseDefaultDatabaseSetup } else { $true }
        UseDefaultReplicaSync = if ($definition.ContainsKey('UseDefaultReplicaSync')) { [bool]$definition.UseDefaultReplicaSync } else { $true }
        UseDefaultCleanup = if ($definition.ContainsKey('UseDefaultCleanup')) { [bool]$definition.UseDefaultCleanup } else { $true }
        SetupSteps = if ($definition.ContainsKey('Setup')) { @($definition.Setup) } else { @() }
        ActionSteps = if ($definition.ContainsKey('Action')) { @($definition.Action) } else { @() }
        VerifySteps = if ($definition.ContainsKey('Verify')) { @($definition.Verify) } else { @() }
        CleanupSteps = if ($definition.ContainsKey('Cleanup')) { @($definition.Cleanup) } else { @() }
    }
}

Export-ModuleMember -Function New-ChoboDeclarativeTestDefinition, Invoke-ChoboDeclarativeSteps, Invoke-ChoboDefaultDatabaseSetup, Invoke-ChoboDefaultReplicaSync, Invoke-ChoboDefaultCleanup
