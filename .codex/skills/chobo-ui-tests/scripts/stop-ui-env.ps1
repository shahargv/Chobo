param(
    [Parameter(Mandatory)] [string]$EnvFile
)

$ErrorActionPreference = 'Stop'
$envInfo = Get-Content -LiteralPath $EnvFile -Raw | ConvertFrom-Json
if (-not $envInfo.ComposeFile -or -not $envInfo.ProjectName) {
    throw "Env file '$EnvFile' does not include ComposeFile and ProjectName."
}

$repoRoot = if ($envInfo.RepoRoot) { [string]$envInfo.RepoRoot } else { [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..')) }
$logsRoot = Join-Path ([string]$envInfo.UiRoot) 'logs'
New-Item -ItemType Directory -Force -Path $logsRoot | Out-Null

$stdout = Join-Path $logsRoot 'stop.stdout.log'
$stderr = Join-Path $logsRoot 'stop.stderr.log'
$arguments = @('compose', '-f', [string]$envInfo.ComposeFile, '-p', [string]$envInfo.ProjectName, 'down', '--remove-orphans', '-v')
$process = Start-Process -FilePath 'docker' -ArgumentList $arguments -WorkingDirectory $repoRoot -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if ($process.ExitCode -ne 0) {
    throw "docker compose down failed with exit code $($process.ExitCode). See $logsRoot"
}

Write-Host "Stopped Chobo UI test environment '$($envInfo.ProjectName)'."

