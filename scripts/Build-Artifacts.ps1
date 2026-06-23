param(
    [string]$Configuration = 'Release',
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot ".artifacts/build/$Configuration"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' | Remove-Item -Force
$checksumPath = Join-Path $outputRoot 'SHA256SUMS.txt'
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

function Build-ChoboWebAssets {
    $webDirectory = Join-Path $repoRoot 'ChoboWeb'
    $webDistDirectory = Join-Path $webDirectory 'dist'

    if (-not (Test-Path -LiteralPath (Join-Path $webDirectory 'package.json'))) {
        throw "ChoboWeb package.json was not found."
    }

    $null = & npm ci --prefix $webDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed for ChoboWeb."
    }

    $null = & npm run build --prefix $webDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed for ChoboWeb."
    }

    $indexPath = Join-Path $webDistDirectory 'index.html'
    if (-not (Test-Path -LiteralPath $indexPath)) {
        throw "ChoboWeb build did not produce $indexPath."
    }

    return $webDistDirectory
}

function Get-ChoboVersionProperties {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        return @()
    }

    $shortSha = ''
    try {
        $shortSha = (& git -C $repoRoot rev-parse --short HEAD).Trim()
    } catch {
    }

    $informationalVersion = if ([string]::IsNullOrWhiteSpace($shortSha)) {
        $Version
    } else {
        "$Version+$shortSha"
    }

    @(
        "-p:Version=$Version",
        "-p:InformationalVersion=$informationalVersion"
    )
}

function Publish-ChoboProject {
    param(
        [Parameter(Mandatory)] [string]$ProjectPath,
        [Parameter(Mandatory)] [string]$Runtime,
        [Parameter(Mandatory)] [string]$OutputName,
        [string]$WebDistPath
    )

    $publishDirectory = Join-Path $outputRoot $OutputName
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    $arguments = @(
        'publish',
        $ProjectPath,
        '-c',
        $Configuration,
        '-r',
        $Runtime,
        '--self-contained',
        'true',
        '-p:PublishSingleFile=true',
        '-o',
        $publishDirectory
    ) + (Get-ChoboVersionProperties)

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath ($Runtime)."
    }

    if (-not [string]::IsNullOrWhiteSpace($WebDistPath)) {
        $wwwroot = Join-Path $publishDirectory 'wwwroot'
        if (Test-Path -LiteralPath $wwwroot) {
            Remove-Item -LiteralPath $wwwroot -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
        Copy-Item -Path (Join-Path $WebDistPath '*') -Destination $wwwroot -Recurse -Force
    }

    $zipPath = Join-Path $outputRoot "$OutputName.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -Force
}

$webDistPath = Build-ChoboWebAssets

Publish-ChoboProject -ProjectPath (Join-Path $repoRoot 'ChoboCli/ChoboCli.csproj') -Runtime 'win-x64' -OutputName 'chobo-cli-win-x64'
Publish-ChoboProject -ProjectPath (Join-Path $repoRoot 'ChoboCli/ChoboCli.csproj') -Runtime 'linux-x64' -OutputName 'chobo-cli-linux-x64'
Publish-ChoboProject -ProjectPath (Join-Path $repoRoot 'ChoboServer/ChoboServer.csproj') -Runtime 'win-x64' -OutputName 'chobo-server-win-x64' -WebDistPath $webDistPath
Publish-ChoboProject -ProjectPath (Join-Path $repoRoot 'ChoboServer/ChoboServer.csproj') -Runtime 'linux-x64' -OutputName 'chobo-server-linux-x64' -WebDistPath $webDistPath

Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
        "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
    } |
    Set-Content -LiteralPath $checksumPath

Write-Host "Artifacts written to $outputRoot"
