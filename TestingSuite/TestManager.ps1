param(
    [string]$TestId,
    [string[]]$TestName = @(),
    [string]$OutputDirectory,
    [int]$GlobalTimeoutSeconds = 1800,
    [int]$TestTimeoutSeconds = 300,
    [switch]$ListTests,
    [switch]$KeepEnvironment,
    [switch]$NoCleanupOnSuccess,
    [switch]$CleanTestResults
)

$ErrorActionPreference = 'Stop'

$SuiteRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent $SuiteRoot
$ComposeFile = $null
$ComposeServices = @()
$HasStorage = $false

if ([string]::IsNullOrWhiteSpace($TestId)) {
    $TestId = "test-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 8))
}

$RunId = $TestId

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepoRoot '.artifacts/TestResults'
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

if ($CleanTestResults -and (Test-Path -LiteralPath $OutputRoot)) {
    $resolvedOutputRoot = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $OutputRoot).Path)
    if ($resolvedOutputRoot -eq [System.IO.Path]::GetPathRoot($resolvedOutputRoot)) {
        throw "Refusing to clean filesystem root '$resolvedOutputRoot'."
    }

    Get-ChildItem -LiteralPath $resolvedOutputRoot -Force | Remove-Item -Recurse -Force
}

$OutputDirectory = [System.IO.Path]::GetFullPath((Join-Path $OutputRoot $TestId))
$LogsDirectory = Join-Path $OutputDirectory 'logs'
New-Item -ItemType Directory -Force -Path $OutputDirectory, $LogsDirectory | Out-Null

$ProjectName = ($RunId -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()
$ProjectName = "chobo$ProjectName"

if ($ListTests) {
    Import-Module (Join-Path $SuiteRoot 'Infra/TestDiscovery.psm1') -Force

    Get-ChoboTests -TestsRoot (Join-Path $SuiteRoot 'Tests') |
        Sort-Object Name |
        ForEach-Object {
            $runAll = if ($_.ExcludeFromRunAll) { 'excluded-from-run-all' } else { 'run-all' }
            "{0}`t{1}`t{2}" -f $_.Name, $runAll, $_.Description
        }

    exit 0
}

function Invoke-ChoboProcess {
    param(
        [Parameter(Mandatory)] [string]$FileName,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 0,
        [string]$StdOutPath,
        [string]$StdErrPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['CHOBO_TEST_OUTPUT'] = $OutputDirectory

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $completed = if ($TimeoutSeconds -gt 0) {
        $process.WaitForExit($TimeoutSeconds * 1000)
    } else {
        $process.WaitForExit()
        $true
    }

    if (-not $completed) {
        try {
            $process.Kill($true)
        } catch {
        }
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    if ($StdOutPath) {
        Set-Content -Path $StdOutPath -Value $stdout -NoNewline
    }
    if ($StdErrPath) {
        Set-Content -Path $StdErrPath -Value $stderr -NoNewline
    }

    [pscustomobject]@{
        ExitCode = if ($completed) { $process.ExitCode } else { 124 }
        TimedOut = -not $completed
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Invoke-ChoboCompose {
    param(
        [Parameter(Mandatory)] [string[]]$ComposeArguments,
        [int]$TimeoutSeconds = 0,
        [string]$Name = 'compose'
    )

    $stdoutPath = Join-Path $LogsDirectory "$Name.stdout.log"
    $stderrPath = Join-Path $LogsDirectory "$Name.stderr.log"
    $arguments = @('-f', $ComposeFile, '-p', $ProjectName) + $ComposeArguments
    Invoke-ChoboProcess -FileName 'docker' -Arguments (@('compose') + $arguments) -WorkingDirectory $RepoRoot -TimeoutSeconds $TimeoutSeconds -StdOutPath $stdoutPath -StdErrPath $stderrPath
}

function Remove-ChoboSystemTestContainers {
    $ps = Invoke-ChoboProcess -FileName 'docker' -Arguments @('ps', '-aq', '--filter', 'label=chobo.system-test=true') -WorkingDirectory $RepoRoot -TimeoutSeconds 60 -StdOutPath (Join-Path $LogsDirectory 'preclean-ps.stdout.log') -StdErrPath (Join-Path $LogsDirectory 'preclean-ps.stderr.log')
    if ($ps.ExitCode -ne 0) {
        Write-Warning "Could not list leftover Chobo system-test containers. See $LogsDirectory."
        return
    }

    $containerIds = @($ps.StdOut -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($containerIds.Count -eq 0) {
        return
    }

    $rm = Invoke-ChoboProcess -FileName 'docker' -Arguments (@('rm', '-f') + $containerIds) -WorkingDirectory $RepoRoot -TimeoutSeconds 180 -StdOutPath (Join-Path $LogsDirectory 'preclean-rm.stdout.log') -StdErrPath (Join-Path $LogsDirectory 'preclean-rm.stderr.log')
    if ($rm.ExitCode -ne 0) {
        Write-Warning "Could not remove all leftover Chobo system-test containers. See $LogsDirectory."
    }
}

function Get-ChoboRunAllTests {
    Import-Module (Join-Path $SuiteRoot 'Infra/TestDiscovery.psm1') -Force
    @(Get-ChoboTests -TestsRoot (Join-Path $SuiteRoot 'Tests') |
        Where-Object { -not $_.ExcludeFromRunAll } |
        Sort-Object Name)
}

function Invoke-ChoboRunAllTests {
    $tests = Get-ChoboRunAllTests
    if ($tests.Count -eq 0) {
        throw 'No matching tests found.'
    }

    $runStarted = Get-Date
    $results = New-Object System.Collections.Generic.List[object]
    $hostPwsh = (Get-Process -Id $PID).Path

    foreach ($test in $tests) {
        $safeName = ($test.Name -replace '[^a-zA-Z0-9-]', '-').ToLowerInvariant()
        $childTestId = "$TestId-$safeName"
        Write-Host "Running $($test.Name) as $childTestId"

        $arguments = @(
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            $PSCommandPath,
            '-TestId',
            $childTestId,
            '-OutputDirectory',
            $OutputDirectory,
            '-TestName',
            $test.Name,
            '-GlobalTimeoutSeconds',
            "$GlobalTimeoutSeconds",
            '-TestTimeoutSeconds',
            "$TestTimeoutSeconds"
        )
        if ($NoCleanupOnSuccess) {
            $arguments += '-NoCleanupOnSuccess'
        }
        if ($KeepEnvironment) {
            $arguments += '-KeepEnvironment'
        }

        $stdoutPath = Join-Path $LogsDirectory "run-all-$safeName.stdout.log"
        $stderrPath = Join-Path $LogsDirectory "run-all-$safeName.stderr.log"
        $run = Invoke-ChoboProcess -FileName $hostPwsh -Arguments $arguments -WorkingDirectory $RepoRoot -TimeoutSeconds $GlobalTimeoutSeconds -StdOutPath $stdoutPath -StdErrPath $stderrPath
        if ($run.StdOut) {
            Write-Host $run.StdOut
        }
        if ($run.StdErr) {
            Write-Error $run.StdErr
        }

        $childOutputDirectory = Join-Path $OutputDirectory $childTestId
        $childResultsPath = Join-Path $childOutputDirectory 'results.json'
        if (Test-Path -LiteralPath $childResultsPath) {
            $childSummary = Get-Content -LiteralPath $childResultsPath -Raw | ConvertFrom-Json
            foreach ($result in @($childSummary.Results)) {
                $results.Add([pscustomobject]@{
                    Test = $result.Test
                    Status = $result.Status
                    TimedOut = [bool]$result.TimedOut
                    Error = $result.Error
                    StartedAt = $result.StartedAt
                    FinishedAt = $result.FinishedAt
                    DurationSeconds = $result.DurationSeconds
                    ArtifactDirectory = Join-Path $childOutputDirectory ("artifacts/{0}" -f $result.Test)
                    RunId = $childTestId
                })
            }
        } else {
            $errorText = if ($run.TimedOut) {
                "Global timeout of $GlobalTimeoutSeconds seconds expired."
            } elseif ($run.StdErr) {
                $run.StdErr
            } else {
                "Test manager failed with exit code $($run.ExitCode)."
            }
            $results.Add([pscustomobject]@{
                Test = $test.Name
                Status = 'Failed'
                TimedOut = [bool]$run.TimedOut
                Error = $errorText
                StartedAt = $null
                FinishedAt = (Get-Date).ToString('o')
                DurationSeconds = $null
                ArtifactDirectory = $childOutputDirectory
                RunId = $childTestId
            })
        }
    }

    $runFinished = Get-Date
    $summary = [pscustomobject]@{
        RunId = $RunId
        StartedAt = $runStarted.ToString('o')
        FinishedAt = $runFinished.ToString('o')
        DurationSeconds = [math]::Round(($runFinished - $runStarted).TotalSeconds, 3)
        TestTimeoutSeconds = $TestTimeoutSeconds
        Total = $results.Count
        Passed = @($results | Where-Object Status -eq 'Passed').Count
        Failed = @($results | Where-Object Status -ne 'Passed').Count
        Results = @($results.ToArray())
    }

    Import-Module (Join-Path $SuiteRoot 'Infra/Reporting.psm1') -Force
    Write-ChoboReports -RunSummary $summary -OutputDirectory $OutputDirectory
    if ($summary.Failed -gt 0) {
        throw "Run-all failed: $($summary.Failed) of $($summary.Total) test(s) failed."
    }
}

function Copy-ChoboComposeLogs {
    foreach ($service in @($ComposeServices)) {
        $result = Invoke-ChoboCompose -ComposeArguments @('logs', '--no-color', $service) -TimeoutSeconds 60 -Name "logs-$service"
        if ($result.ExitCode -ne 0) {
            continue
        }
    }
}

function Copy-ChoboServerFileLogs {
    if ($ComposeServices -notcontains 'choboserver') {
        return
    }

    $destination = Join-Path $LogsDirectory 'choboserver-files'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $result = Invoke-ChoboCompose -ComposeArguments @('cp', 'choboserver:/app/logs/.', $destination) -TimeoutSeconds 60 -Name 'copy-choboserver-file-logs'
    if ($result.ExitCode -ne 0) {
        Write-Warning "Could not copy ChoboServer file logs. See $LogsDirectory."
    }
}

try {
    Write-Host "Chobo system test id: $TestId"
    Write-Host "Output: $OutputDirectory"

    if ($TestName.Count -eq 0) {
        Invoke-ChoboRunAllTests
        exit 0
    }

    Remove-ChoboSystemTestContainers

    Import-Module (Join-Path $SuiteRoot 'Infra/ComposeGenerator.psm1') -Force
    $environment = New-ChoboComposeEnvironment -SuiteRoot $SuiteRoot -RepoRoot $RepoRoot -OutputDirectory $OutputDirectory -TestName $TestName
    $ComposeFile = $environment.ComposeFile
    $ComposeServices = @($environment.Services)
    $HasStorage = [bool]$environment.HasStorage
    Write-Host "Generated compose: $ComposeFile"
    Write-Host "Services: $($ComposeServices -join ', ')"

    $preDown = Invoke-ChoboCompose -ComposeArguments @('down', '--remove-orphans', '-v') -TimeoutSeconds 180 -Name 'compose-predown'
    if ($preDown.ExitCode -ne 0) {
        Write-Warning "Pre-run Docker Compose cleanup failed. Continuing with startup. See $LogsDirectory."
    }

    $up = Invoke-ChoboCompose -ComposeArguments @('up', '-d', '--build') -TimeoutSeconds $GlobalTimeoutSeconds -Name 'compose-up'
    if ($up.ExitCode -ne 0) {
        throw "Docker Compose startup failed. See $LogsDirectory."
    }

    if ($HasStorage) {
        $minioInit = Invoke-ChoboCompose -ComposeArguments @('run', '--rm', 'minio-init') -TimeoutSeconds 180 -Name 'minio-init'
        if ($minioInit.ExitCode -ne 0) {
            throw "MinIO initialization failed. See $LogsDirectory."
        }
    }

    $runnerArgs = @(
        'exec',
        '-T',
        'test-runner',
        'pwsh',
        '/suite/Runner/Run-Tests.ps1',
        '-RunId',
        $RunId,
        '-OutputDirectory',
        '/results',
        '-TestTimeoutSeconds',
        "$TestTimeoutSeconds"
    )

    if ($ListTests) {
        $runnerArgs += '-ListTests'
    }
    if ($TestName.Count -gt 0) {
        $runnerArgs += @('-TestName', ($TestName -join ','))
    }
    if ($NoCleanupOnSuccess) {
        $runnerArgs += '-NoCleanupOnSuccess'
    }

    $run = Invoke-ChoboCompose -ComposeArguments $runnerArgs -TimeoutSeconds $GlobalTimeoutSeconds -Name 'test-runner'
    if ($run.StdOut) {
        Write-Host $run.StdOut
    }
    if ($run.StdErr) {
        Write-Error $run.StdErr
    }
    if ($run.TimedOut) {
        throw "Global timeout of $GlobalTimeoutSeconds seconds expired."
    }
    if ($run.ExitCode -ne 0) {
        throw "Test runner failed with exit code $($run.ExitCode)."
    }
} finally {
    if ($ComposeFile) {
        Copy-ChoboComposeLogs
        Copy-ChoboServerFileLogs
    }

    if (-not $KeepEnvironment -and $ComposeFile) {
        $down = Invoke-ChoboCompose -ComposeArguments @('down', '--remove-orphans', '-v') -TimeoutSeconds 180 -Name 'compose-down'
        if ($down.ExitCode -ne 0) {
            Write-Warning "Docker Compose cleanup failed. See $LogsDirectory."
        }
        Remove-ChoboSystemTestContainers
    } elseif ($KeepEnvironment -and $ComposeFile) {
        Write-Host "Environment kept: docker compose -f `"$ComposeFile`" -p $ProjectName ps"
    }

}

Write-Host "Results: $OutputDirectory"
