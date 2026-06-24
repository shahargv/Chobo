param(
    [ValidateSet('bootstrap','cluster','storage','policy','schedule-edit','backup','restore','details','logs-audit','failure','full','large-table')]
    [string]$Scenario = 'full',
    [string]$TestId,
    [int]$HostPort = 0,
    [switch]$KeepEnvironment
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
if ([string]::IsNullOrWhiteSpace($TestId)) {
    $TestId = 'ui-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 8))
}

$RunRoot = Join-Path $RepoRoot ".artifacts\TestResults\$TestId"
$UiRoot = Join-Path $RunRoot 'ui'
$ComposeRoot = Join-Path $UiRoot 'compose'
$LogsRoot = Join-Path $UiRoot 'logs'
New-Item -ItemType Directory -Force -Path $ComposeRoot, $LogsRoot | Out-Null

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

if ($HostPort -le 0) { $HostPort = Get-FreeTcpPort }
$ProjectName = ('choboui' + ($TestId -replace '[^a-zA-Z0-9]', '')).ToLowerInvariant()
if ($ProjectName.Length -gt 48) { $ProjectName = $ProjectName.Substring(0, 48) }
$isLargeTableScenario = $Scenario -eq 'large-table'
$retentionInterval = if ($isLargeTableScenario) { '12:00:00' } else { '00:00:01' }
$garbageCollectorInterval = if ($isLargeTableScenario) { '12:00:00' } else { '00:00:01' }
$sourceSetupTimeoutSeconds = if ($isLargeTableScenario) { 7200 } else { 60 }

$composeFile = Join-Path $ComposeRoot 'docker-compose.ui.yml'
$repoPath = $RepoRoot.Replace('\', '/')
$compose = @"
services:
  minio:
    labels:
      chobo.ui-test: "true"
    image: minio/minio:RELEASE.2025-09-07T16-13-09Z
    command: ["server", "/data", "--console-address", ":9001"]
    environment:
      MINIO_ROOT_USER: chobo-access-key
      MINIO_ROOT_PASSWORD: chobo-secret-key
    networks:
      chobo-ui-tests:
        aliases:
          - backup-s3

  minio-init:
    labels:
      chobo.ui-test: "true"
    image: minio/mc:RELEASE.2025-08-13T08-35-41Z
    depends_on:
      - minio
    entrypoint:
      - /bin/sh
      - -c
      - |
        until mc alias set chobo http://minio:9000 chobo-access-key chobo-secret-key; do sleep 1; done
        mc mb --ignore-existing chobo/data-bucket
    networks:
      - chobo-ui-tests

  clickhouse-source:
    labels:
      chobo.ui-test: "true"
    image: clickhouse/clickhouse-server:26.3
    environment:
      CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: "1"
    ulimits:
      nofile:
        soft: 262144
        hard: 262144
    networks:
      - chobo-ui-tests

  clickhouse-restore:
    labels:
      chobo.ui-test: "true"
    image: clickhouse/clickhouse-server:26.3
    environment:
      CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: "1"
    ulimits:
      nofile:
        soft: 262144
        hard: 262144
    networks:
      - chobo-ui-tests

  choboserver:
    labels:
      chobo.ui-test: "true"
    build:
      context: "$repoPath"
      dockerfile: ChoboServer/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: SystemTest
      ASPNETCORE_URLS: http://0.0.0.0:8080
      CHOBO_INIT_ADMIN_USER: ""
      CHOBO_INIT_ACCESS_TOKEN: ""
      CHOBO_TEST_HOOKS_ENABLED: "true"
      Chobo__DataDirectory: /tmp/chobo-data
      Chobo__BackupRestore__SchedulerInterval: "00:00:01"
      Chobo__BackupRestore__PollInterval: "00:00:01"
      Chobo__RetentionManagement__Interval: "$retentionInterval"
      Chobo__RetentionManagement__MaxDop: "2"
      Chobo__BackupsGarbageCollector__Interval: "$garbageCollectorInterval"
      Chobo__BackupsGarbageCollector__MaxDop: "2"
      Chobo__BackupStorageOperations__S3RequestTimeout: "00:10:00"
      Chobo__BackupStorageOperations__S3MaxErrorRetry: "5"
    ports:
      - "127.0.0.1:$HostPort`:8080"
    depends_on:
      - clickhouse-source
      - clickhouse-restore
      - minio
    networks:
      - chobo-ui-tests

networks:
  chobo-ui-tests:
    driver: bridge
"@
$compose | Set-Content -LiteralPath $composeFile -Encoding UTF8

function Invoke-Compose {
    param([Parameter(Mandatory)] [string[]]$Args, [string]$Name = 'compose', [int]$TimeoutSeconds = 0)
    $stdout = Join-Path $LogsRoot "$Name.stdout.log"
    $stderr = Join-Path $LogsRoot "$Name.stderr.log"
    $processArgs = @('compose', '-f', $composeFile, '-p', $ProjectName) + $Args
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'docker'
    foreach ($arg in $processArgs) { [void]$psi.ArgumentList.Add($arg) }
    $psi.WorkingDirectory = $RepoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::new()
    $p.StartInfo = $psi
    [void]$p.Start()
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $ok = if ($TimeoutSeconds -gt 0) { $p.WaitForExit($TimeoutSeconds * 1000) } else { $p.WaitForExit(); $true }
    if (-not $ok) { try { $p.Kill($true) } catch {} }
    $out = $outTask.GetAwaiter().GetResult()
    $err = $errTask.GetAwaiter().GetResult()
    Set-Content -LiteralPath $stdout -Value $out -NoNewline
    Set-Content -LiteralPath $stderr -Value $err -NoNewline
    if (-not $ok) { throw "docker compose $Name timed out. See $LogsRoot" }
    if ($p.ExitCode -ne 0) { throw "docker compose $Name failed with exit code $($p.ExitCode). See $LogsRoot" }
    [pscustomobject]@{ StdOut = $out; StdErr = $err; ExitCode = $p.ExitCode }
}

Invoke-Compose -Args @('down', '--remove-orphans', '-v') -Name 'predown' -TimeoutSeconds 180 | Out-Null
Invoke-Compose -Args @('up', '-d', '--build') -Name 'up' -TimeoutSeconds 1800 | Out-Null
Invoke-Compose -Args @('run', '--rm', 'minio-init') -Name 'minio-init' -TimeoutSeconds 180 | Out-Null

$deadline = (Get-Date).AddSeconds(120)
do {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$HostPort/health" -TimeoutSec 3
        if ($health.status -eq 'ok') { break }
    } catch {}
    Start-Sleep -Seconds 2
} while ((Get-Date) -lt $deadline)
if ((Get-Date) -ge $deadline) { throw "ChoboServer did not become healthy at http://127.0.0.1:$HostPort" }

$setupSql = @"
DROP DATABASE IF EXISTS backup_single_source SYNC;
CREATE DATABASE IF NOT EXISTS backup_single_source ENGINE = Atomic;
CREATE TABLE backup_single_source.source_orders
(
    id UInt32,
    name String
)
ENGINE = MergeTree
ORDER BY id;
INSERT INTO backup_single_source.source_orders (id, name) VALUES
(1, 'alpha'),
(2, 'beta'),
(3, 'gamma');
"@
$setupSqlPath = Join-Path $UiRoot 'source-setup.sql'
$setupSql | Set-Content -LiteralPath $setupSqlPath -Encoding UTF8

$clickhouseReady = $false
$deadline = (Get-Date).AddSeconds(120)
do {
    try {
        Invoke-Compose -Args @('exec', '-T', 'clickhouse-source', 'clickhouse-client', '-q', 'SELECT 1') -Name 'clickhouse-ready' -TimeoutSeconds 20 | Out-Null
        $clickhouseReady = $true
    } catch { Start-Sleep -Seconds 2 }
} while (-not $clickhouseReady -and (Get-Date) -lt $deadline)
if (-not $clickhouseReady) { throw 'clickhouse-source did not become ready.' }

if ($isLargeTableScenario) {
    $setupSql = @"
CREATE DATABASE IF NOT EXISTS large_ontime_source ENGINE = Atomic;
DROP TABLE IF EXISTS large_ontime_source.ontime SYNC;
CREATE TABLE large_ontime_source.ontime
ENGINE = MergeTree
ORDER BY (Year, Quarter, Month, DayofMonth, FlightDate, IATA_CODE_Reporting_Airline)
AS
SELECT *
FROM s3('https://clickhouse-public-datasets.s3.amazonaws.com/ontime/csv_by_year/{2000..2010}.csv.gz', 'CSVWithNames')
SETTINGS
    input_format_csv_empty_as_default = 1,
    schema_inference_make_columns_nullable = 0,
    max_insert_threads = 8;
OPTIMIZE TABLE large_ontime_source.ontime FINAL;
"@
    $setupSql | Set-Content -LiteralPath $setupSqlPath -Encoding UTF8
}

Invoke-Compose -Args @('exec', '-T', 'clickhouse-source', 'clickhouse-client', '--multiquery', '-q', $setupSql) -Name 'source-setup' -TimeoutSeconds $sourceSetupTimeoutSeconds | Out-Null

$envFile = Join-Path $UiRoot 'ui-env.json'
$envInfo = [ordered]@{
    TestId = $TestId
    Scenario = $Scenario
    RepoRoot = $RepoRoot
    UiRoot = $UiRoot
    ComposeFile = $composeFile
    ProjectName = $ProjectName
    BaseUrl = "http://127.0.0.1:$HostPort"
    HostPort = $HostPort
    KeepEnvironment = [bool]$KeepEnvironment
    AuthToken = $null
}
$envInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $envFile -Encoding UTF8

$result = [pscustomobject]@{
    TestId = $TestId
    Scenario = $Scenario
    BaseUrl = "http://127.0.0.1:$HostPort"
    UiRoot = $UiRoot
    EnvFile = $envFile
    ComposeFile = $composeFile
    ProjectName = $ProjectName
}
$result | ConvertTo-Json -Depth 5 | Write-Host
$result


