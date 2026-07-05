param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$FromTag,

    [int]$Port = 19080,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) {
    Write-Host "[release-sample] $Message"
}

function Invoke-CommandChecked([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Info "$FilePath $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-Version([string]$Value) {
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Version must be SemVer-like X.Y.Z without a leading v. Received '$Value'."
    }
}

function Wait-ChoboServer([string]$ServerUrl, [Diagnostics.Process]$Process) {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        if ($Process.HasExited) {
            throw "ChoboServer exited before becoming healthy. Exit code: $($Process.ExitCode)."
        }

        try {
            Invoke-RestMethod -Uri "$ServerUrl/health" -TimeoutSec 2 | Out-Null
            return
        }
        catch {
            Start-Sleep -Seconds 1
        }
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for ChoboServer at $ServerUrl."
}

function Start-ChoboServerForSample([string]$Root, [string]$DataDirectory, [int]$Port, [string]$LogPath) {
    $serverDll = [System.IO.Path]::Combine($Root, 'ChoboServer', 'bin', 'Release', 'net10.0', 'ChoboServer.dll')
    if (-not (Test-Path -LiteralPath $serverDll)) {
        throw "Server binary not found at $serverDll. Build the solution first."
    }

    $oldEnvironment = @{
        ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
        ASPNETCORE_URLS = $env:ASPNETCORE_URLS
        CHOBO_DATA_DIRECTORY = $env:CHOBO_DATA_DIRECTORY
        CHOBO_ENCRYPTION_KEY_BASE64 = $env:CHOBO_ENCRYPTION_KEY_BASE64
        CHOBO_INIT_ADMIN_USER = $env:CHOBO_INIT_ADMIN_USER
        CHOBO_INIT_ACCESS_TOKEN = $env:CHOBO_INIT_ACCESS_TOKEN
        CHOBO_TEST_HOOKS_ENABLED = $env:CHOBO_TEST_HOOKS_ENABLED
        CHOBO_WEB_IS_GUI_ENABLED = $env:CHOBO_WEB_IS_GUI_ENABLED
        CHOBO_SQLITE_JOURNAL_MODE = $env:CHOBO_SQLITE_JOURNAL_MODE
    }

    $env:ASPNETCORE_ENVIRONMENT = 'SystemTest'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
    $env:CHOBO_DATA_DIRECTORY = $DataDirectory
    $env:CHOBO_ENCRYPTION_KEY_BASE64 = 'MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY='
    $env:CHOBO_INIT_ADMIN_USER = 'admin'
    $env:CHOBO_INIT_ACCESS_TOKEN = 'release-sample-token'
    $env:CHOBO_TEST_HOOKS_ENABLED = 'true'
    $env:CHOBO_WEB_IS_GUI_ENABLED = 'false'
    $env:CHOBO_SQLITE_JOURNAL_MODE = 'DELETE'

    try {
        $outLog = [System.IO.Path]::ChangeExtension($LogPath, '.out.log')
        $errLog = [System.IO.Path]::ChangeExtension($LogPath, '.err.log')
        $process = Start-Process -FilePath 'dotnet' -ArgumentList @($serverDll) -WorkingDirectory ([System.IO.Path]::Combine($Root, 'ChoboServer')) -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden -PassThru
    }
    finally {
        foreach ($key in $oldEnvironment.Keys) {
            if ($null -eq $oldEnvironment[$key]) {
                Remove-Item "env:$key" -ErrorAction SilentlyContinue
            }
            else {
                Set-Item "env:$key" $oldEnvironment[$key]
            }
        }
    }

    return $process
}

function Stop-ProcessQuietly([Diagnostics.Process]$Process) {
    if ($null -eq $Process -or $Process.HasExited) {
        return
    }

    $Process.CloseMainWindow() | Out-Null
    if (-not $Process.WaitForExit(5000)) {
        $Process.Kill($true)
        $Process.WaitForExit()
    }
}

function New-SampleFromRoot([string]$Root, [string]$Version, [string]$SourceRef, [int]$Port, [string]$OutputDirectory, [switch]$DryRun) {
    $sampleDirectory = Join-Path $OutputDirectory $Version
    if (Test-Path -LiteralPath $sampleDirectory) {
        throw "Sample directory already exists: $sampleDirectory"
    }

    if ($DryRun) {
        Write-Info "Dry run: would build $SourceRef and create $sampleDirectory."
        return
    }

    Invoke-CommandChecked 'dotnet' @('build', 'Chobo.sln', '-c', 'Release', "/p:Version=$Version", "/p:InformationalVersion=$Version+release-sample") $Root

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "chobo-release-sample-$([Guid]::NewGuid().ToString('N'))"
    $dataDirectory = Join-Path $tempRoot 'data'
    $logPath = Join-Path $tempRoot 'server.log'
    New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null

    $server = $null
    try {
        $server = Start-ChoboServerForSample $Root $dataDirectory $Port $logPath
        $serverUrl = "http://127.0.0.1:$Port"
        Wait-ChoboServer $serverUrl $server

        $cliDll = [System.IO.Path]::Combine($Root, 'ChoboCli', 'bin', 'Release', 'net10.0', 'ChoboCli.dll')
        Invoke-CommandChecked 'dotnet' @($cliDll, 'test-hooks', 'seed-export-import-graph', '--server-url', $serverUrl, '--access-token', 'release-sample-token') $Root

        New-Item -ItemType Directory -Force -Path $sampleDirectory | Out-Null
        Invoke-CommandChecked 'dotnet' @($cliDll, 'config', 'export', '--output', (Join-Path $sampleDirectory 'config-export.json'), '--server-url', $serverUrl, '--access-token', 'release-sample-token') $Root
        Invoke-CommandChecked 'dotnet' @($cliDll, 'data', 'export', '--output', (Join-Path $sampleDirectory 'data-export.json'), '--server-url', $serverUrl, '--access-token', 'release-sample-token') $Root

        Stop-ProcessQuietly $server
        $server = $null
        Copy-Item -LiteralPath (Join-Path $dataDirectory 'chobo.db') -Destination (Join-Path $sampleDirectory 'chobo.db')

        $versionResponse = Get-Content -LiteralPath (Join-Path $sampleDirectory 'data-export.json') -Raw | ConvertFrom-Json
        $manifest = [ordered]@{
            version = $Version
            sourceRef = $SourceRef
            generatedAt = (Get-Date).ToUniversalTime().ToString('O')
            generator = 'scripts/New-ReleaseDbSample.ps1'
            seed = 'test-hooks seed-export-import-graph'
            exportVersion = $versionResponse.exportVersion
            schemaVersion = $versionResponse.schemaVersion
            productVersion = $versionResponse.productVersion
        }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $sampleDirectory 'sample-manifest.json') -Encoding UTF8
        Write-Info "Created $sampleDirectory."
    }
    finally {
        Stop-ProcessQuietly $server
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

Assert-Version $Version
$repoRoot = Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..'))
$outputDirectory = [System.IO.Path]::Combine($repoRoot, '.release', 'db-samples')
if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

if ($FromTag) {
    if ($FromTag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw "-FromTag must look like vX.Y.Z. Received '$FromTag'."
    }

    $worktreeRoot = Join-Path ([System.IO.Path]::GetTempPath()) "chobo-release-sample-worktree-$([Guid]::NewGuid().ToString('N'))"
    try {
        if (-not $DryRun) {
            Invoke-CommandChecked 'git' @('worktree', 'add', '--detach', $worktreeRoot, $FromTag) $repoRoot
        }

        New-SampleFromRoot $worktreeRoot $Version $FromTag $Port $outputDirectory -DryRun:$DryRun
    }
    finally {
        if ((Test-Path -LiteralPath $worktreeRoot) -and -not $DryRun) {
            Invoke-CommandChecked 'git' @('worktree', 'remove', '--force', $worktreeRoot) $repoRoot
        }
    }
}
else {
    New-SampleFromRoot $repoRoot $Version 'HEAD' $Port $outputDirectory -DryRun:$DryRun
}
