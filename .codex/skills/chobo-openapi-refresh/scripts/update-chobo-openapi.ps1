param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path,
    [int]$Port = 5087,
    [switch]$NoBuild,
    [switch]$SkipTypecheck
)

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$webRoot = Join-Path $repo 'ChoboWeb'
$snapshot = Join-Path $webRoot 'openapi/chobo.v1.json'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "chobo-openapi-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$out = Join-Path $tmp 'server.out.log'
$err = Join-Path $tmp 'server.err.log'
$url = "http://127.0.0.1:$Port/swagger/v1/swagger.json"

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'Chobo.sln') -v minimal -m:1 --no-restore
}

$oldEnv = @{
    ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
    ASPNETCORE_URLS = $env:ASPNETCORE_URLS
    Chobo__DataDirectory = $env:Chobo__DataDirectory
    CHOBO_INIT_ACCESS_TOKEN = $env:CHOBO_INIT_ACCESS_TOKEN
    CHOBO_TEST_HOOKS_ENABLED = $env:CHOBO_TEST_HOOKS_ENABLED
}

$process = $null
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
    $env:Chobo__DataDirectory = $tmp
    $env:CHOBO_INIT_ACCESS_TOKEN = 'openapi-refresh-token'
    Remove-Item Env:CHOBO_TEST_HOOKS_ENABLED -ErrorAction SilentlyContinue

    $args = @('run', '--project', (Join-Path $repo 'ChoboServer/ChoboServer.csproj'), '--no-launch-profile')
    if ($NoBuild) { $args += '--no-build' }
    $process = Start-Process dotnet -ArgumentList $args -WorkingDirectory $repo -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru

    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($process.HasExited) {
            throw "ChoboServer exited before Swagger was available.`nstdout:`n$(Get-Content $out -Raw)`nstderr:`n$(Get-Content $err -Raw)"
        }

        try {
            Invoke-WebRequest -Uri $url -OutFile $snapshot -UseBasicParsing
            $ready = $true
            break
        } catch {
            Start-Sleep -Seconds 1
        }
    }

    if (-not $ready) {
        throw "Timed out waiting for $url.`nstdout:`n$(Get-Content $out -Raw)`nstderr:`n$(Get-Content $err -Raw)"
    }
} finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }

    foreach ($name in $oldEnv.Keys) {
        if ($null -eq $oldEnv[$name]) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        } else {
            Set-Item "Env:$name" $oldEnv[$name]
        }
    }
}

Push-Location $webRoot
try {
    npm run generate:api
    if (-not $SkipTypecheck) {
        npm run typecheck
    }
} finally {
    Pop-Location
}

$badPatterns = @('test-hooks', 'metrics/prometheus', 'prometheusMetrics')
foreach ($pattern in $badPatterns) {
    $matches = & rg -n --fixed-strings $pattern (Join-Path $repo 'ChoboWeb/openapi/chobo.v1.json') (Join-Path $repo 'ChoboWeb/src')
    if ($LASTEXITCODE -eq 0) {
        throw "Stale forbidden OpenAPI/client text '$pattern' remains:`n$matches"
    }
    if ($LASTEXITCODE -gt 1) {
        throw "rg failed while checking '$pattern'."
    }
}

Write-Host "Updated Chobo OpenAPI snapshot and generated TypeScript from $url."
