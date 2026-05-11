param(
    [Parameter(Mandatory)] [string]$RunId,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [string[]]$TestName = @(),
    [int]$TestTimeoutSeconds = 300,
    [switch]$ListTests,
    [switch]$NoCleanupOnSuccess
)

$ErrorActionPreference = 'Stop'

$SuiteRoot = '/suite'
$InfraRoot = Join-Path $SuiteRoot 'Infra'
$TestsRoot = Join-Path $SuiteRoot 'Tests'

Import-Module (Join-Path $InfraRoot 'TestDiscovery.psm1') -Force
Import-Module (Join-Path $InfraRoot 'Reporting.psm1') -Force
Import-Module (Join-Path $InfraRoot 'ResourceRequirements.psm1') -Force

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$tests = Get-ChoboTests -TestsRoot $TestsRoot
$TestName = @($TestName | ForEach-Object { ([string]$_).Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) } | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

if ($TestName.Count -gt 0) {
    $nameSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $TestName) {
        [void]$nameSet.Add($name)
    }
    $tests = @($tests | Where-Object { $nameSet.Contains($_.Name) })
} else {
    $tests = @($tests | Where-Object { -not $_.ExcludeFromRunAll })
}

if ($ListTests) {
    $tests | Sort-Object Name | ForEach-Object { "{0}`t{1}" -f $_.Name, $_.Description }
    exit 0
}

if ($tests.Count -eq 0) {
    throw 'No matching tests found.'
}

$runStarted = Get-Date
$testResults = New-Object System.Collections.Generic.List[object]

foreach ($test in $tests) {
    $testTimeout = if ($test.TimeoutSeconds) { [int]$test.TimeoutSeconds } else { $TestTimeoutSeconds }
    $testOutput = Join-Path $OutputDirectory ("artifacts/{0}" -f $test.Name)
    New-Item -ItemType Directory -Force -Path $testOutput | Out-Null

    $job = Start-ThreadJob -ArgumentList $SuiteRoot, $test.Path, $test.Name, $RunId, $testOutput, $NoCleanupOnSuccess.IsPresent -ScriptBlock {
        param($SuiteRoot, $TestPath, $TestName, $RunId, $TestOutput, $NoCleanupOnSuccess)

        $ErrorActionPreference = 'Stop'
        $InfraRoot = Join-Path $SuiteRoot 'Infra'
        Import-Module (Join-Path $InfraRoot 'ClickHouse.psm1') -Force
        Import-Module (Join-Path $InfraRoot 'Assertions.psm1') -Force
        Import-Module (Join-Path $InfraRoot 'TestContext.psm1') -Force
        Import-Module (Join-Path $InfraRoot 'ChoboCli.psm1') -Force
        Import-Module (Join-Path $InfraRoot 'DeclarativeTests.psm1') -Force
        Import-Module (Join-Path $InfraRoot 'ResourceRequirements.psm1') -Force

        if ($TestPath.EndsWith('.psd1', [System.StringComparison]::OrdinalIgnoreCase)) {
            $definition = New-ChoboDeclarativeTestDefinition -DefinitionPath $TestPath
        } else {
            . $TestPath
            $definition = Get-ChoboTestDefinition
        }

        $resourceDefinitions = Get-ChoboResourceDefinitions -TestDefinition $definition
        $context = New-ChoboTestContext -RunId $RunId -TestName $TestName -OutputDirectory $TestOutput -ResourceDefinitions $resourceDefinitions
        Register-ChoboResourceDnsAliases -Context $context

        $started = Get-Date
        $status = 'Passed'
        $errorMessage = $null

        try {
            Wait-ChoboClickHouse -Context $context -TimeoutSeconds 120
            if ($definition.Kind -eq 'Declarative') {
                if ($definition.UseDefaultDatabaseSetup) {
                    Invoke-ChoboDefaultDatabaseSetup -Context $context
                }
                Invoke-ChoboDeclarativeSteps -Context $context -TestRoot $definition.TestRoot -Steps @($definition.SetupSteps)
                if ($definition.UseDefaultReplicaSync) {
                    Invoke-ChoboDefaultReplicaSync -Context $context
                }
                Invoke-ChoboDeclarativeSteps -Context $context -TestRoot $definition.TestRoot -Steps @($definition.ActionSteps)
                Invoke-ChoboDeclarativeSteps -Context $context -TestRoot $definition.TestRoot -Steps @($definition.VerifySteps)
            } else {
                if ($definition.Setup) {
                    & $definition.Setup $context
                }
                if ($definition.Action) {
                    & $definition.Action $context
                }
                if ($definition.Verify) {
                    & $definition.Verify $context
                }
            }
        } catch {
            $status = 'Failed'
            $errorMessage = $_.Exception.Message
            Set-Content -Path (Join-Path $TestOutput 'failure.txt') -Value ($_.Exception.ToString())
        } finally {
            if ($status -eq 'Passed' -and -not $NoCleanupOnSuccess) {
                try {
                    if ($definition.Kind -eq 'Declarative') {
                        Invoke-ChoboDeclarativeSteps -Context $context -TestRoot $definition.TestRoot -Steps @($definition.CleanupSteps)
                        if ($definition.UseDefaultCleanup) {
                            Invoke-ChoboDefaultCleanup -Context $context
                        }
                    } elseif ($definition.Cleanup) {
                        & $definition.Cleanup $context
                    }
                } catch {
                    $status = 'Failed'
                    $errorMessage = "Cleanup failed: $($_.Exception.Message)"
                    Set-Content -Path (Join-Path $TestOutput 'cleanup-failure.txt') -Value ($_.Exception.ToString())
                }
            }
        }

        $finished = Get-Date
        [pscustomobject]@{
            Test = $TestName
            Status = $status
            TimedOut = $false
            Error = $errorMessage
            StartedAt = $started.ToString('o')
            FinishedAt = $finished.ToString('o')
            DurationSeconds = [math]::Round(($finished - $started).TotalSeconds, 3)
            ArtifactDirectory = $TestOutput
        }
    }

    $completed = Wait-Job -Job $job -Timeout $testTimeout
    if (-not $completed) {
        Stop-Job -Job $job
        $testResults.Add([pscustomobject]@{
            Test = $test.Name
            Status = 'Failed'
            TimedOut = $true
            Error = "Per-test timeout of $testTimeout seconds expired."
            StartedAt = $null
            FinishedAt = (Get-Date).ToString('o')
            DurationSeconds = $testTimeout
            ArtifactDirectory = $testOutput
        })
    } else {
        $received = Receive-Job -Job $job
        foreach ($item in $received) {
            $testResults.Add($item)
        }
    }
    Remove-Job -Job $job -Force
}

$runFinished = Get-Date
$summary = [pscustomobject]@{
    RunId = $RunId
    StartedAt = $runStarted.ToString('o')
    FinishedAt = $runFinished.ToString('o')
    DurationSeconds = [math]::Round(($runFinished - $runStarted).TotalSeconds, 3)
    TestTimeoutSeconds = $TestTimeoutSeconds
    Total = $testResults.Count
    Passed = @($testResults | Where-Object Status -eq 'Passed').Count
    Failed = @($testResults | Where-Object Status -ne 'Passed').Count
    Results = @($testResults.ToArray())
}

Write-ChoboReports -RunSummary $summary -OutputDirectory $OutputDirectory

if ($summary.Failed -gt 0) {
    exit 1
}

exit 0
