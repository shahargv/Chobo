function Get-ChoboDeclarativeTokens {
    param(
        [Parameter(Mandatory)] $Context
    )

    $tokens = @{
        RunId = $Context.RunId
        TestName = $Context.TestName
        TestId = $Context.TestId
    }

    Ensure-ChoboContextVariables -Context $Context
    foreach ($variable in $Context.Variables.GetEnumerator()) {
        Add-ChoboJsonTokens -Tokens $tokens -Prefix $variable.Key -Value $variable.Value
    }

    foreach ($resourceName in $Context.Resources.PSObject.Properties.Name) {
        $resource = $Context.Resources.$resourceName
        $tokens["$resourceName.Name"] = $resource.Name
        $tokens["$resourceName.Type"] = $resource.Type
        $tokens["$resourceName.Shards"] = $resource.Shards
        $tokens["$resourceName.Replicas"] = $resource.Replicas
        $tokens["$resourceName.Host"] = $resource.Host
        $tokens["$resourceName.DnsName"] = $resource.DnsName
        $tokens["$resourceName.ClusterName"] = $resource.ClusterName
        $tokens["$resourceName.ReplicaHost"] = $resource.ReplicaHost
        $tokens["$resourceName.Endpoint"] = $resource.Endpoint
        $tokens["$resourceName.Bucket"] = $resource.Bucket
        $tokens["$resourceName.AccessKey"] = $resource.AccessKey
        $tokens["$resourceName.SecretKey"] = $resource.SecretKey
    }

    $tokens
}

function Ensure-ChoboContextVariables {
    param(
        [Parameter(Mandatory)] $Context
    )

    if ($Context.PSObject.Properties.Name -notcontains 'Variables') {
        $Context | Add-Member -NotePropertyName Variables -NotePropertyValue @{} -Force
    }
}

function Add-ChoboJsonTokens {
    param(
        [Parameter(Mandatory)] [hashtable]$Tokens,
        [Parameter(Mandatory)] [string]$Prefix,
        $Value
    )

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [System.Array]) {
        $Tokens["$Prefix.count"] = $Value.Count
        return
    }

    foreach ($property in $Value.PSObject.Properties) {
        $key = "$Prefix.$($property.Name)"
        $Tokens[$key] = $property.Value
        if ($property.Value -isnot [string] -and $property.Value -isnot [ValueType]) {
            Add-ChoboJsonTokens -Tokens $Tokens -Prefix $key -Value $property.Value
        }
    }
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

function Get-ChoboCliStepArguments {
    param(
        [Parameter(Mandatory)] $Step,
        [Parameter(Mandatory)] [string]$TestRoot,
        [Parameter(Mandatory)] [hashtable]$Tokens
    )

    if ($Step.ContainsKey('Args')) {
        return @($Step.Args | ForEach-Object { Expand-ChoboTemplate -Text ([string]$_) -Tokens $Tokens })
    }

    if ($Step.ContainsKey('Command')) {
        $command = Expand-ChoboTemplate -Text $Step.Command -Tokens $Tokens
        return @(Split-ChoboCliCommandLine -Command $command)
    }

    $command = Get-ChoboStepText -Step $Step -TestRoot $TestRoot -Tokens $Tokens
    return @(Split-ChoboCliCommandLine -Command $command)
}

function Get-ChoboJsonPathValue {
    param(
        $Json,
        [string]$Path = '$'
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -eq '$') {
        return $Json
    }

    $current = $Json
    $segments = $Path.TrimStart('$').TrimStart('.').Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    foreach ($segment in $segments) {
        if ($null -eq $current) {
            return $null
        }

        if ($segment -match '^\[(\d+)\]$') {
            $current = @($current)[[int]$Matches[1]]
            continue
        }

        if ($segment -match '^([^\[]+)\[(\d+)\]$') {
            $propertyName = $Matches[1]
            $current = $current.PSObject.Properties[$propertyName].Value
            $current = @($current)[[int]$Matches[2]]
            continue
        }

        $property = $current.PSObject.Properties[$segment]
        $current = if ($property) { $property.Value } else { $null }
    }

    $current
}

function ConvertTo-ChoboComparableValue {
    param(
        $Value,
        [hashtable]$Tokens
    )

    if ($Value -is [string]) {
        return Expand-ChoboTemplate -Text $Value -Tokens $Tokens
    }

    $Value
}

function Assert-ChoboJsonExpectation {
    param(
        $Json,
        [Parameter(Mandatory)] [hashtable]$Expectation,
        [Parameter(Mandatory)] [hashtable]$Tokens
    )

    $path = if ($Expectation.ContainsKey('Path')) { $Expectation.Path } else { '$' }
    $value = Get-ChoboJsonPathValue -Json $Json -Path $path

    if ($Expectation.ContainsKey('Equals')) {
        $expected = ConvertTo-ChoboComparableValue -Value $Expectation.Equals -Tokens $Tokens
        if ([string]$value -ne [string]$expected) {
            throw "JSON expectation failed at '$path'. Expected '$expected', actual '$value'."
        }
    }

    if ($Expectation.ContainsKey('NotEmpty')) {
        if ($Expectation.NotEmpty -and [string]::IsNullOrWhiteSpace([string]$value)) {
            throw "JSON expectation failed at '$path'. Expected a non-empty value."
        }
    }

    if ($Expectation.ContainsKey('Count')) {
        $expectedCount = [int](ConvertTo-ChoboComparableValue -Value $Expectation.Count -Tokens $Tokens)
        $actualCount = @($value).Count
        if ($actualCount -ne $expectedCount) {
            throw "JSON expectation failed at '$path'. Expected count $expectedCount, actual $actualCount."
        }
    }

    if ($Expectation.ContainsKey('Contains')) {
        $expectedItem = ConvertTo-ChoboComparableValue -Value $Expectation.Contains -Tokens $Tokens
        if ($value -is [string]) {
            if (-not $value.Contains([string]$expectedItem)) {
                throw "JSON expectation failed at '$path'. Expected text containing '$expectedItem'."
            }
        } elseif (-not (@($value) | Where-Object { [string]$_ -eq [string]$expectedItem })) {
            throw "JSON expectation failed at '$path'. Expected collection containing '$expectedItem'."
        }
    }

    if ($Expectation.ContainsKey('ContainsObject')) {
        $expectedProperties = $Expectation.ContainsObject
        $match = @($value) | Where-Object {
            $candidate = $_
            foreach ($entry in $expectedProperties.GetEnumerator()) {
                $actualProperty = $candidate.PSObject.Properties[$entry.Key]
                $expectedValue = ConvertTo-ChoboComparableValue -Value $entry.Value -Tokens $Tokens
                if (-not $actualProperty -or [string]$actualProperty.Value -ne [string]$expectedValue) {
                    return $false
                }
            }
            return $true
        } | Select-Object -First 1

        if (-not $match) {
            throw "JSON expectation failed at '$path'. Expected an object containing the declared properties."
        }
    }
}

function Assert-ChoboCliStepResult {
    param(
        [Parameter(Mandatory)] $Step,
        [Parameter(Mandatory)] $Result,
        [Parameter(Mandatory)] [hashtable]$Tokens
    )

    $expectedExitCode = if ($Step.ContainsKey('ExpectExitCode')) { [int]$Step.ExpectExitCode } else { 0 }
    if ($Result.ExitCode -ne $expectedExitCode) {
        throw "CLI exit code expectation failed. Expected $expectedExitCode, actual $($Result.ExitCode). Output: $($Result.OutputText)"
    }

    if ($Step.ContainsKey('ExpectTextContains')) {
        foreach ($expectedText in @($Step.ExpectTextContains)) {
            $expanded = Expand-ChoboTemplate -Text ([string]$expectedText) -Tokens $Tokens
            if (-not $Result.OutputText.Contains($expanded)) {
                throw "CLI text expectation failed. Expected output containing '$expanded'."
            }
        }
    }

    if ($Step.ContainsKey('ExpectTextNotContains')) {
        foreach ($unexpectedText in @($Step.ExpectTextNotContains)) {
            $expanded = Expand-ChoboTemplate -Text ([string]$unexpectedText) -Tokens $Tokens
            if ($Result.OutputText.Contains($expanded)) {
                throw "CLI text expectation failed. Output must not contain '$expanded'."
            }
        }
    }

    if ($Step.ContainsKey('ExpectJson')) {
        if ($null -eq $Result.Json) {
            throw "CLI JSON expectation failed. Output was not valid JSON: $($Result.OutputText)"
        }

        foreach ($expectation in @($Step.ExpectJson)) {
            Assert-ChoboJsonExpectation -Json $Result.Json -Expectation $expectation -Tokens $Tokens
        }
    }
}

function Invoke-ChoboDeclarativeCliStep {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string]$TestRoot,
        [Parameter(Mandatory)] $Step,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [hashtable]$Tokens
    )

    $arguments = Get-ChoboCliStepArguments -Step $Step -TestRoot $TestRoot -Tokens $Tokens
    $commandPath = Join-Path $Context.OutputDirectory "$Name.cli.txt"
    Set-Content -Path $commandPath -Value ($arguments -join ' ')

    $deadline = if ($Step.ContainsKey('RetryTimeoutSeconds')) { (Get-Date).AddSeconds([int]$Step.RetryTimeoutSeconds) } else { Get-Date }
    $interval = if ($Step.ContainsKey('RetryIntervalSeconds')) { [int]$Step.RetryIntervalSeconds } else { 2 }
    $lastError = $null
    $result = $null

    do {
        $result = Invoke-ChoboCliCommand -Arguments $arguments
        Set-Content -Path (Join-Path $Context.OutputDirectory "$Name.out.txt") -Value $result.OutputText
        try {
            Assert-ChoboCliStepResult -Step $Step -Result $result -Tokens $Tokens
            $lastError = $null
            break
        } catch {
            $lastError = $_.Exception.Message
            if ((Get-Date) -ge $deadline) {
                break
            }
            Start-Sleep -Seconds $interval
        }
    } while ((Get-Date) -lt $deadline)

    if ($lastError) {
        throw $lastError
    }

    if ($Step.ContainsKey('SaveJsonAs')) {
        if ($null -eq $result.Json) {
            throw "Cannot save CLI output '$($Step.SaveJsonAs)' because it was not valid JSON."
        }

        Ensure-ChoboContextVariables -Context $Context
        $Context.Variables[$Step.SaveJsonAs] = $result.Json
    }
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
                Invoke-ChoboDeclarativeCliStep -Context $Context -TestRoot $TestRoot -Step $step -Name $name -Tokens $tokens
                $tokens = Get-ChoboDeclarativeTokens -Context $Context
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
        if ($resource.Type -eq 'S3' -or $resource.Type -eq 'ChoboServer') {
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
        if ($resource.Type -eq 'S3' -or $resource.Type -eq 'ChoboServer') {
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
        UseDefaultDatabaseSetup = if ($definition.ContainsKey('UseDefaultDatabaseSetup')) { [bool]$definition.UseDefaultDatabaseSetup } else { $false }
        UseDefaultReplicaSync = if ($definition.ContainsKey('UseDefaultReplicaSync')) { [bool]$definition.UseDefaultReplicaSync } else { $true }
        UseDefaultCleanup = if ($definition.ContainsKey('UseDefaultCleanup')) { [bool]$definition.UseDefaultCleanup } else { $true }
        SetupSteps = if ($definition.ContainsKey('Setup')) { @($definition.Setup) } else { @() }
        ActionSteps = if ($definition.ContainsKey('Action')) { @($definition.Action) } else { @() }
        VerifySteps = if ($definition.ContainsKey('Verify')) { @($definition.Verify) } else { @() }
        CleanupSteps = if ($definition.ContainsKey('Cleanup')) { @($definition.Cleanup) } else { @() }
    }
}

Export-ModuleMember -Function New-ChoboDeclarativeTestDefinition, Invoke-ChoboDeclarativeSteps, Invoke-ChoboDefaultDatabaseSetup, Invoke-ChoboDefaultReplicaSync, Invoke-ChoboDefaultCleanup
