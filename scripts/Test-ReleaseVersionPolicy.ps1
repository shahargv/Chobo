param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$BaseTag,

    [switch]$AllowLegacyFirstRelease,

    [switch]$DryRun,

    [int]$MockBaseSchemaVersion,

    [int]$MockCurrentSchemaVersion,

    [switch]$MockLikelySchemaChange
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) {
    Write-Host "[release-version] $Message"
}

function Invoke-Git([string[]]$Arguments) {
    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $output"
    }

    return $output
}

function Get-ReleaseVersionParts([string]$Value) {
    if ($Value -notmatch '^(?:v)?(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$') {
        throw "Release version must be SemVer-like X.Y.Z without a leading v. Received '$Value'."
    }

    [pscustomobject]@{
        Major = [int]$Matches.major
        Minor = [int]$Matches.minor
        Patch = [int]$Matches.patch
        Text = $Value.TrimStart('v')
    }
}

function Get-ChoboApiSchemaVersionFromText([string]$Text, [string]$Source) {
    if ($Text -notmatch 'public\s+const\s+int\s+SchemaVersion\s*=\s*(?<schema>[0-9]+)\s*;') {
        throw "Could not read ChoboApi.SchemaVersion from $Source."
    }

    return [int]$Matches.schema
}

function Get-CurrentSchemaVersion {
    $path = [System.IO.Path]::Combine($PSScriptRoot, '..', 'Chobo.Contracts', 'ChoboApi.cs')
    return Get-ChoboApiSchemaVersionFromText (Get-Content -LiteralPath $path -Raw) $path
}

function Get-TagSchemaVersion([string]$Tag) {
    $text = Invoke-Git @('show', "$Tag`:Chobo.Contracts/ChoboApi.cs")
    return Get-ChoboApiSchemaVersionFromText ($text -join [Environment]::NewLine) "$Tag`:Chobo.Contracts/ChoboApi.cs"
}

function Get-LatestReleaseTag([string]$ExcludeTag) {
    $tags = Invoke-Git @('tag', '--list', 'v[0-9]*.[0-9]*.[0-9]*', '--sort=-v:refname')
    foreach ($tag in $tags) {
        if ($ExcludeTag -and $tag -eq $ExcludeTag) {
            continue
        }

        if ($tag -match '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
            return $tag
        }
    }

    return $null
}

function Get-ChangedSchemaFiles([string]$Tag) {
    $files = Invoke-Git @(
        'diff',
        '--name-only',
        "$Tag..HEAD",
        '--',
        'ChoboServer/Data/*Entity.cs',
        'ChoboServer/Data/ChoboDbContext.cs',
        'ChoboServer/Data/Migrations',
        'ChoboServer/Services/DatabaseBootstrap.cs',
        'ChoboServer/Services/SchemaUpgradeService.cs'
    )

    return @($files | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-DiffLooksSchemaAffecting([string]$Tag, [string]$Path) {
    if ($Path -like 'ChoboServer/Data/Migrations/*') {
        return [pscustomobject]@{ Impact = 'Likely'; Reason = 'EF migration file changed.' }
    }

    $diff = Invoke-Git @('diff', '--unified=0', "$Tag..HEAD", '--', $Path)
    $changedLines = @($diff | Where-Object { $_ -match '^[+-][^+-]' })
    $joined = $changedLines -join [Environment]::NewLine

    if ($Path -like 'ChoboServer/Data/*Entity.cs' -and $joined -match '(?im)^[+-]\s*public\s+[^()=;{]+\s+\w+\s*\{\s*get;\s*set;') {
        return [pscustomobject]@{ Impact = 'Likely'; Reason = 'Entity persisted property was added, removed, or changed.' }
    }

    if ($Path -eq 'ChoboServer/Data/ChoboDbContext.cs' -and $joined -match '\.(Entity|Property|HasKey|HasIndex|HasOne|HasMany|OwnsOne|ToTable|HasColumn|HasConversion|IsRequired|HasMaxLength|OnDelete)\b') {
        return [pscustomobject]@{ Impact = 'Likely'; Reason = 'DbContext model configuration changed.' }
    }

    if ($Path -in @('ChoboServer/Services/DatabaseBootstrap.cs', 'ChoboServer/Services/SchemaUpgradeService.cs')) {
        return [pscustomobject]@{ Impact = 'Ambiguous'; Reason = 'Startup or schema upgrade behavior changed; human release review required.' }
    }

    if ($changedLines.Count -gt 0) {
        return [pscustomobject]@{ Impact = 'Ambiguous'; Reason = 'Schema-sensitive file changed, but static review could not prove SQLite shape impact.' }
    }

    return [pscustomobject]@{ Impact = 'None'; Reason = 'No relevant diff lines.' }
}

Set-Location (Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..')))

$versionParts = Get-ReleaseVersionParts $Version
if ($Version.StartsWith('v')) {
    throw "Pass release versions without a leading v. Use '$($versionParts.Text)', not '$Version'."
}

if (-not $BaseTag) {
    $BaseTag = Get-LatestReleaseTag "v$($versionParts.Text)"
}

if (-not $BaseTag) {
    throw 'No prior release tag was found. Pass -BaseTag or create an initial release tag first.'
}

$baseParts = Get-ReleaseVersionParts $BaseTag
$currentSchemaVersion = if ($PSBoundParameters.ContainsKey('MockCurrentSchemaVersion')) { $MockCurrentSchemaVersion } else { Get-CurrentSchemaVersion }
$baseSchemaVersion = if ($PSBoundParameters.ContainsKey('MockBaseSchemaVersion')) { $MockBaseSchemaVersion } else { Get-TagSchemaVersion $BaseTag }
$schemaVersionChanged = $currentSchemaVersion -ne $baseSchemaVersion
$minorChanged = $versionParts.Minor -ne $baseParts.Minor
$majorChanged = $versionParts.Major -ne $baseParts.Major

Write-Info "Version: $($versionParts.Text)"
Write-Info "Base tag: $BaseTag"
Write-Info "SchemaVersion: $baseSchemaVersion -> $currentSchemaVersion"

$changedFiles = Get-ChangedSchemaFiles $BaseTag
$reviews = @()
foreach ($file in $changedFiles) {
    $analysis = Test-DiffLooksSchemaAffecting $BaseTag $file
    $reviews += [pscustomobject]@{
        File = $file
        Impact = $analysis.Impact
        Reason = $analysis.Reason
    }
}

if ($MockLikelySchemaChange) {
    $reviews += [pscustomobject]@{
        File = '<mocked schema-sensitive diff>'
        Impact = 'Likely'
        Reason = 'Dry-run rehearsal simulated a SQLite schema-affecting change.'
    }
}

if ($reviews.Count -eq 0) {
    Write-Info 'Schema-sensitive changed files: none.'
}
else {
    Write-Info 'Schema-sensitive changed files:'
    foreach ($review in $reviews) {
        Write-Host "  - [$($review.Impact)] $($review.File): $($review.Reason)"
    }
}

$likelySchemaChange = @($reviews | Where-Object Impact -eq 'Likely').Count -gt 0
$ambiguousSchemaChange = @($reviews | Where-Object Impact -eq 'Ambiguous').Count -gt 0
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

if ($majorChanged) {
    if ($versionParts.Minor -ne 0) {
        $failures.Add("New major releases must reset schema minor Y to 0.")
    }
}
elseif ($schemaVersionChanged) {
    if ($versionParts.Minor -ne ($baseParts.Minor + 1) -or $versionParts.Patch -ne 0) {
        $failures.Add("SchemaVersion changed, so release version should be $($baseParts.Major).$($baseParts.Minor + 1).0.")
    }
}
else {
    if ($minorChanged) {
        $failures.Add("ChoboApi.SchemaVersion did not change, so release minor Y must stay $($baseParts.Minor).")
    }

    if ($versionParts.Patch -le $baseParts.Patch -and $versionParts.Minor -eq $baseParts.Minor -and $versionParts.Major -eq $baseParts.Major) {
        $failures.Add("Same-schema release patch Z must be greater than previous patch $($baseParts.Patch).")
    }
}

if ($likelySchemaChange -and -not $schemaVersionChanged) {
    $failures.Add("Likely SQLite schema changes were detected, but ChoboApi.SchemaVersion did not increase.")
}

if ($schemaVersionChanged -and -not $likelySchemaChange -and -not $AllowLegacyFirstRelease) {
    $warnings.Add("ChoboApi.SchemaVersion changed, but no clearly schema-affecting diff was detected in the selected paths.")
}

if ($ambiguousSchemaChange) {
    $warnings.Add("Ambiguous schema-sensitive changes were detected. Notify the release owner before publishing.")
}

if ($schemaVersionChanged -or $likelySchemaChange -or $ambiguousSchemaChange) {
    Write-Warning 'Release schema advisory: review the findings above with the release owner before publishing.'
}
else {
    Write-Info 'Release schema advisory: no SQLite schema bump appears necessary.'
}

foreach ($warning in $warnings) {
    Write-Warning $warning
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure
    }

    throw "Release version policy failed with $($failures.Count) error(s)."
}

if ($DryRun) {
    Write-Info 'Dry run complete. No repository or remote state was changed.'
}

Write-Info 'Release version policy passed.'
