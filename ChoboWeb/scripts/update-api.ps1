param(
    [string]$ServerUrl = "http://localhost:8080",
    [switch]$SkipDownload
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$snapshot = Join-Path $root "openapi/chobo.v1.json"

if (-not $SkipDownload) {
    $url = "$($ServerUrl.TrimEnd('/'))/swagger/v1/swagger.json"
    Invoke-WebRequest -Uri $url -OutFile $snapshot
}

Push-Location $root
try {
    npm run generate:api
    npm run typecheck
} finally {
    Pop-Location
}
