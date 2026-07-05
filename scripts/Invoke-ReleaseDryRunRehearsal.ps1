param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$SchemaChange,

    [switch]$SkipUpgradeSamples
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) {
    Write-Host "[release-rehearsal] $Message"
}

function Get-CurrentSchemaVersion {
    $path = [System.IO.Path]::Combine($PSScriptRoot, '..', 'Chobo.Contracts', 'ChoboApi.cs')
    $text = Get-Content -LiteralPath $path -Raw
    if ($text -notmatch 'public\s+const\s+int\s+SchemaVersion\s*=\s*(?<schema>[0-9]+)\s*;') {
        throw "Could not read ChoboApi.SchemaVersion from $path."
    }

    return [int]$Matches.schema
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Version must be SemVer-like X.Y.Z without a leading v. Received '$Version'."
}

$repoRoot = Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..'))
Set-Location $repoRoot

Write-Info "Starting dry-run release rehearsal for $Version."
if ($SchemaChange) {
    Write-Info 'Scenario: schema-change release candidate.'
    $baseSchema = Get-CurrentSchemaVersion
    & (Join-Path $PSScriptRoot 'Test-ReleaseVersionPolicy.ps1') -Version $Version -DryRun -MockBaseSchemaVersion $baseSchema -MockCurrentSchemaVersion ($baseSchema + 1) -MockLikelySchemaChange
}
else {
    Write-Info 'Scenario: same-schema release candidate.'
    & (Join-Path $PSScriptRoot 'Test-ReleaseVersionPolicy.ps1') -Version $Version -DryRun
}
if ($LASTEXITCODE -ne 0) {
    throw 'Release version policy dry run failed.'
}

if (-not $SkipUpgradeSamples) {
    & (Join-Path $PSScriptRoot 'Test-UpgradeSamples.ps1') -Version $Version -DryRun
    if ($LASTEXITCODE -ne 0) {
        throw 'Upgrade sample validation dry run failed.'
    }
}
else {
    Write-Info 'Skipping upgrade sample dry run by request.'
}

& (Join-Path $PSScriptRoot 'New-ReleaseDbSample.ps1') -Version $Version -DryRun
if ($LASTEXITCODE -ne 0) {
    throw 'Sample generation dry run failed.'
}

Write-Info 'Dry-run release rehearsal passed. No tags were pushed, no workflows were dispatched, and no durable repository changes were made.'
