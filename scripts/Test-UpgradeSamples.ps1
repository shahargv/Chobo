param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [int]$Port = 19180,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) {
    Write-Host "[upgrade-samples] $Message"
}

function Assert-Version([string]$Value) {
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Version must be SemVer-like X.Y.Z without a leading v. Received '$Value'."
    }
}

function Get-VersionParts([string]$Value) {
    if ($Value -notmatch '^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)') {
        throw "Invalid version '$Value'."
    }

    [pscustomobject]@{ Major = [int]$Matches.major; Minor = [int]$Matches.minor; Patch = [int]$Matches.patch; Text = $Value }
}

function Get-LatestPriorMinorSample([string]$SamplesRoot, [string]$Version) {
    $target = Get-VersionParts $Version
    $candidates = @()
    if (Test-Path -LiteralPath $SamplesRoot) {
        foreach ($directory in Get-ChildItem -LiteralPath $SamplesRoot -Directory) {
            if ($directory.Name -match '^[0-9]+\.[0-9]+\.[0-9]+$') {
                $parts = Get-VersionParts $directory.Name
                $isPrior = $parts.Major -lt $target.Major -or ($parts.Major -eq $target.Major -and $parts.Minor -lt $target.Minor)
                if ($isPrior) {
                    $candidates += [pscustomobject]@{ Directory = $directory; Parts = $parts }
                }
            }
        }
    }

    return $candidates |
        Sort-Object @{ Expression = { $_.Parts.Major }; Descending = $true }, @{ Expression = { $_.Parts.Minor }; Descending = $true }, @{ Expression = { $_.Parts.Patch }; Descending = $true } |
        Select-Object -First 1
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

function Start-ChoboServerForUpgrade([string]$Root, [string]$DataDirectory, [int]$Port, [string]$LogPath) {
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
        CHOBO_WEB_IS_GUI_ENABLED = $env:CHOBO_WEB_IS_GUI_ENABLED
        CHOBO_SQLITE_JOURNAL_MODE = $env:CHOBO_SQLITE_JOURNAL_MODE
    }

    $env:ASPNETCORE_ENVIRONMENT = 'SystemTest'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
    $env:CHOBO_DATA_DIRECTORY = $DataDirectory
    $env:CHOBO_ENCRYPTION_KEY_BASE64 = 'MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY='
    $env:CHOBO_INIT_ADMIN_USER = 'admin'
    $env:CHOBO_INIT_ACCESS_TOKEN = 'release-upgrade-token'
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

function Invoke-WithServer([string]$Root, [string]$DataDirectory, [int]$Port, [scriptblock]$Action) {
    $logPath = Join-Path $DataDirectory 'server.log'
    $server = $null
    try {
        $server = Start-ChoboServerForUpgrade $Root $DataDirectory $Port $logPath
        $serverUrl = "http://127.0.0.1:$Port"
        Wait-ChoboServer $serverUrl $server
        & $Action $serverUrl
    }
    finally {
        Stop-ProcessQuietly $server
    }
}

Assert-Version $Version
$repoRoot = Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..'))
$samplesRoot = [System.IO.Path]::Combine($repoRoot, '.release', 'db-samples')
$sample = Get-LatestPriorMinorSample $samplesRoot $Version

if (-not $sample) {
    Write-Warning "No prior-minor upgrade sample found under $samplesRoot. Skipping upgrade sample validation."
    return
}

$sampleDirectory = $sample.Directory.FullName
foreach ($required in @('chobo.db', 'config-export.json', 'data-export.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sampleDirectory $required))) {
        throw "Sample $($sample.Directory.Name) is missing $required."
    }
}

if ($DryRun) {
    Write-Info "Dry run: would validate sample $($sample.Directory.Name) against $Version."
    return
}

Invoke-CommandChecked 'dotnet' @('build', 'Chobo.sln', '-c', 'Release', "/p:Version=$Version", "/p:InformationalVersion=$Version+upgrade-samples") $repoRoot
$cliDll = [System.IO.Path]::Combine($repoRoot, 'ChoboCli', 'bin', 'Release', 'net10.0', 'ChoboCli.dll')
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "chobo-upgrade-samples-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    $upgradeData = Join-Path $tempRoot 'upgrade-db'
    New-Item -ItemType Directory -Force -Path $upgradeData | Out-Null
    Copy-Item -LiteralPath (Join-Path $sampleDirectory 'chobo.db') -Destination (Join-Path $upgradeData 'chobo.db')
    Invoke-WithServer $repoRoot $upgradeData $Port {
        param($serverUrl)
        $versionInfo = Invoke-RestMethod -Uri "$serverUrl/api/v1/server/version" -Headers @{ Authorization = 'Bearer release-upgrade-token' } -TimeoutSec 10
        if ($versionInfo.databaseSchemaVersion -ne $versionInfo.schemaVersion) {
            throw "Database schema version $($versionInfo.databaseSchemaVersion) did not upgrade to server schema $($versionInfo.schemaVersion)."
        }
    }

    $configImportData = Join-Path $tempRoot 'config-import'
    New-Item -ItemType Directory -Force -Path $configImportData | Out-Null
    Invoke-WithServer $repoRoot $configImportData ($Port + 1) {
        param($serverUrl)
        Invoke-CommandChecked 'dotnet' @($cliDll, 'config', 'import', '--file', (Join-Path $sampleDirectory 'config-export.json'), '--server-url', $serverUrl, '--access-token', 'release-upgrade-token') $repoRoot
        $clustersJson = & dotnet $cliDll clusters list --server-url $serverUrl --access-token release-upgrade-token
        if ($LASTEXITCODE -ne 0) { throw 'clusters list failed after config import.' }
        if (-not (($clustersJson | ConvertFrom-Json) | Where-Object name -eq 'system-export-cluster')) {
            throw 'Config import did not restore the representative cluster.'
        }
    }

    $dataImportData = Join-Path $tempRoot 'data-import'
    New-Item -ItemType Directory -Force -Path $dataImportData | Out-Null
    Invoke-WithServer $repoRoot $dataImportData ($Port + 2) {
        param($serverUrl)
        Invoke-CommandChecked 'dotnet' @($cliDll, 'data', 'import', '--file', (Join-Path $sampleDirectory 'data-export.json'), '--server-url', $serverUrl, '--access-token', 'release-upgrade-token') $repoRoot

        $export = Get-Content -LiteralPath (Join-Path $sampleDirectory 'data-export.json') -Raw | ConvertFrom-Json
        $backupId = @($export.data.backups)[0].id
        $restoreId = @($export.data.restores)[0].id
        $clusters = (& dotnet $cliDll clusters list --server-url $serverUrl --access-token release-upgrade-token) | ConvertFrom-Json
        $targets = (& dotnet $cliDll targets list --server-url $serverUrl --access-token release-upgrade-token) | ConvertFrom-Json
        $policies = (& dotnet $cliDll policies list --server-url $serverUrl --access-token release-upgrade-token) | ConvertFrom-Json
        $schedules = (& dotnet $cliDll schedules list --server-url $serverUrl --access-token release-upgrade-token) | ConvertFrom-Json
        $backup = (& dotnet $cliDll backups show --id $backupId --server-url $serverUrl --access-token release-upgrade-token) | ConvertFrom-Json
        $restore = (& dotnet $cliDll restores show --id $restoreId --server-url $serverUrl --access-token release-upgrade-token) | ConvertFrom-Json
        $audit = (& dotnet $cliDll audit show --last 20 --server-url $serverUrl --access-token release-upgrade-token) | ConvertFrom-Json

        if (-not ($clusters | Where-Object name -eq 'system-export-cluster')) { throw 'Data import missing representative cluster.' }
        if (-not ($targets | Where-Object name -eq 'system-export-target')) { throw 'Data import missing representative target.' }
        if (-not ($policies | Where-Object name -eq 'system-export-policy')) { throw 'Data import missing representative policy.' }
        if (-not ($schedules | Where-Object name -eq 'system-export-schedule')) { throw 'Data import missing representative schedule.' }
        if ($backup.status -ne 'Succeeded' -or @($backup.tables).Count -lt 1) { throw 'Data import missing representative backup details.' }
        if ($restore.status -ne 'Succeeded' -or @($restore.tables).Count -lt 1) { throw 'Data import missing representative restore details.' }
        if (-not ($audit.items | Where-Object { $_.action -eq 'import' -and $_.entityType -eq 'data' })) { throw 'Data import audit entry was not found.' }
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Info "Upgrade sample validation passed for sample $($sample.Directory.Name)."
