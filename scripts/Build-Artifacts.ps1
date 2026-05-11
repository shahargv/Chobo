param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot ".artifacts/build/$Configuration"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

dotnet publish (Join-Path $repoRoot 'ChoboCli/ChoboCli.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $outputRoot 'cli-win-x64')
dotnet publish (Join-Path $repoRoot 'ChoboCli/ChoboCli.csproj') -c $Configuration -r linux-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $outputRoot 'cli-linux-x64')
dotnet publish (Join-Path $repoRoot 'ChoboServer/ChoboServer.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $outputRoot 'server-win-x64')
dotnet publish (Join-Path $repoRoot 'ChoboServer/ChoboServer.csproj') -c $Configuration -r linux-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $outputRoot 'server-linux-x64')

docker build -f (Join-Path $repoRoot 'ChoboServer/Dockerfile') -t choboserver:local $repoRoot
docker build -f (Join-Path $repoRoot 'ChoboCli/Dockerfile') -t chobocli:local $repoRoot

Write-Host "Artifacts written to $outputRoot"
