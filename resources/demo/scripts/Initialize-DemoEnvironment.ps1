param(
    [double]$BigTableTargetGb = $(if ([string]::IsNullOrWhiteSpace($env:CHOBO_DEMO_BIG_TABLE_TARGET_GB)) { 5 } else { [double]$env:CHOBO_DEMO_BIG_TABLE_TARGET_GB }),
    [int]$RowsPerInsertBatch = 1000000,
    [switch]$SkipSeed,
    [string]$OutputDirectory = '/demo-output'
)

$ErrorActionPreference = 'Stop'

$ChoboInternalUrl = $env:CHOBO_INTERNAL_URL
if ([string]::IsNullOrWhiteSpace($ChoboInternalUrl)) { $ChoboInternalUrl = 'http://choboserver:8080' }
$ChoboPublicUrl = $env:CHOBO_PUBLIC_URL
if ([string]::IsNullOrWhiteSpace($ChoboPublicUrl)) { $ChoboPublicUrl = 'http://host.docker.internal:18080' }
$MinioInternalEndpoint = $env:MINIO_INTERNAL_ENDPOINT
if ([string]::IsNullOrWhiteSpace($MinioInternalEndpoint)) { $MinioInternalEndpoint = 'http://minio:9000' }
$MinioPublicEndpoint = $env:MINIO_PUBLIC_ENDPOINT
if ([string]::IsNullOrWhiteSpace($MinioPublicEndpoint)) { $MinioPublicEndpoint = 'http://host.docker.internal:19000' }
$MinioConsoleUrl = $env:MINIO_CONSOLE_URL
if ([string]::IsNullOrWhiteSpace($MinioConsoleUrl)) { $MinioConsoleUrl = 'http://host.docker.internal:19001' }
$AccessToken = $env:CHOBO_ACCESS_TOKEN
if ([string]::IsNullOrWhiteSpace($AccessToken)) { $AccessToken = 'demo-static-access-token' }
$MinioAccessKey = $env:MINIO_ACCESS_KEY
if ([string]::IsNullOrWhiteSpace($MinioAccessKey)) { $MinioAccessKey = 'chobo-access-key' }
$MinioSecretKey = $env:MINIO_SECRET_KEY
if ([string]::IsNullOrWhiteSpace($MinioSecretKey)) { $MinioSecretKey = 'chobo-secret-key' }
$Bucket = $env:MINIO_BUCKET
if ([string]::IsNullOrWhiteSpace($Bucket)) { $Bucket = 'data-bucket' }
$ClusterName = 'chobo_demo_cluster'
$DatabaseName = 'demo'
$ClickHouseNodes = @('clickhouse-s1-r1', 'clickhouse-s1-r2', 'clickhouse-s2-r1', 'clickhouse-s2-r2')
$LargeLocalTables = @('local_ontime', 'local_ontime_events')
$LargeDistributedTables = @('dist_ontime', 'dist_ontime_events')
$CommonCliArgs = @('--server-url', $ChoboInternalUrl, '--access-token', $AccessToken)

function Wait-Until([string]$Name, [scriptblock]$Action, [int]$TimeoutSeconds = 300) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    do {
        try {
            & $Action
            Write-Host "$Name is ready."
            return
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 2
        }
    } while ((Get-Date) -lt $deadline)
    throw "$Name did not become ready within $TimeoutSeconds seconds. Last error: $lastError"
}

function Invoke-ChoboCli([string[]]$Arguments) {
    $output = & ChoboCli @Arguments 2>&1
    $text = ($output | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "ChoboCli failed: ChoboCli $($Arguments -join ' ')`n$text" }
    $text
}

function ConvertFrom-CommandJson([string]$Text) {
    $trimmed = $Text.Trim()
    $objectStart = $trimmed.IndexOf('{')
    $arrayStart = $trimmed.IndexOf('[')
    $start = if ($objectStart -lt 0) { $arrayStart } elseif ($arrayStart -lt 0) { $objectStart } else { [Math]::Min($objectStart, $arrayStart) }
    if ($start -lt 0) { throw "Command did not return JSON: $Text" }
    $trimmed.Substring($start) | ConvertFrom-Json
}

function Invoke-ClickHouse([string]$HostName, [string]$Query) {
    $output = & clickhouse-client --host $HostName --multiquery --format TSVRaw --query $Query 2>&1
    $text = ($output | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "ClickHouse query failed on ${HostName}: $Query`n$text" }
    $text
}

function Assert-HttpReachable([string]$Name, [string]$Uri, [hashtable]$Headers = @{}) {
    $response = Invoke-WebRequest -Uri $Uri -Headers $Headers -UseBasicParsing -TimeoutSec 20
    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 400) {
        throw "$Name returned HTTP $($response.StatusCode) at $Uri."
    }
    [ordered]@{ Name = $Name; Uri = $Uri; Status = 'Passed'; StatusCode = [int]$response.StatusCode }
}

function Assert-PositiveCount([string]$Node, [string]$Table) {
    $countText = Invoke-ClickHouse $Node "SELECT count() FROM $Table"
    $count = [int64]$countText
    if ($count -le 0) { throw "$Table on $Node has no rows." }
    [ordered]@{ Node = $Node; Table = $Table; Rows = $count; Status = 'Passed' }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Wait-Until 'MinIO' { & mc alias set chobo $MinioInternalEndpoint $MinioAccessKey $MinioSecretKey | Out-Null }
& mc mb --ignore-existing "chobo/$Bucket" | Out-Null

Wait-Until 'ClickHouse cluster entrypoint' { Invoke-ClickHouse 'clickhouse-s1-r1' 'SELECT 1' | Out-Null }
Wait-Until 'Chobo API' { Invoke-RestMethod -Uri "$ChoboInternalUrl/health" -TimeoutSec 5 | Out-Null }

$schemaSql = @"
DROP DATABASE IF EXISTS $DatabaseName ON CLUSTER $ClusterName SYNC;
CREATE DATABASE $DatabaseName ON CLUSTER $ClusterName ENGINE = Replicated('/clickhouse/databases/{uuid}', '{shard}', '{replica}');

CREATE TABLE $DatabaseName.local_ontime
(
    flight_date Date,
    year UInt16,
    month UInt8,
    day_of_month UInt8,
    reporting_airline LowCardinality(String),
    origin LowCardinality(String),
    dest LowCardinality(String),
    flight_num UInt32,
    dep_delay Int16,
    arr_delay Int16,
    distance UInt16,
    payload String
)
ENGINE = ReplicatedMergeTree
PARTITION BY toYYYYMM(flight_date)
ORDER BY (year, month, day_of_month, reporting_airline, origin, dest, flight_num);

CREATE TABLE $DatabaseName.dist_ontime AS $DatabaseName.local_ontime
ENGINE = Distributed($ClusterName, $DatabaseName, local_ontime, cityHash64(origin, dest, flight_num));

CREATE TABLE $DatabaseName.local_ontime_events
(
    event_time DateTime,
    event_date Date,
    carrier LowCardinality(String),
    airport LowCardinality(String),
    event_type LowCardinality(String),
    aircraft_id UInt32,
    route_id UInt32,
    metric_1 Float64,
    metric_2 Float64,
    payload String
)
ENGINE = ReplicatedMergeTree
PARTITION BY toYYYYMM(event_date)
ORDER BY (event_date, carrier, airport, event_type, aircraft_id);

CREATE TABLE $DatabaseName.dist_ontime_events AS $DatabaseName.local_ontime_events
ENGINE = Distributed($ClusterName, $DatabaseName, local_ontime_events, cityHash64(carrier, airport, aircraft_id));
"@

for ($i = 1; $i -le 10; $i++) {
    $schemaSql += @"

CREATE TABLE $DatabaseName.local_small_$i
(
    id UInt64,
    tenant_id UInt32,
    name String,
    created_at DateTime,
    amount Decimal(18, 2)
)
ENGINE = ReplicatedMergeTree
ORDER BY (tenant_id, id);

CREATE TABLE $DatabaseName.dist_small_$i AS $DatabaseName.local_small_$i
ENGINE = Distributed($ClusterName, $DatabaseName, local_small_$i, cityHash64(tenant_id, id));
"@
}

$schemaPath = Join-Path $OutputDirectory 'schema.sql'
$schemaSql | Set-Content -LiteralPath $schemaPath -Encoding UTF8
Invoke-ClickHouse 'clickhouse-s1-r1' $schemaSql | Out-Null

if (-not $SkipSeed) {
    $smallSeedSql = ''
    for ($i = 1; $i -le 10; $i++) {
        $smallSeedSql += @"
INSERT INTO $DatabaseName.dist_small_$i
SELECT number + 1, (number % 25) + 1, concat('small-$i-', toString(number)), now() - toIntervalSecond(number % 86400), toDecimal64((number % 100000) / 100, 2)
FROM numbers(10000);

"@
    }
    Invoke-ClickHouse 'clickhouse-s1-r1' $smallSeedSql | Out-Null

    $targetBytes = [int64]($BigTableTargetGb * 1024 * 1024 * 1024)
    $targetRows = [Math]::Max($RowsPerInsertBatch, [int64][Math]::Ceiling($targetBytes / 512))
    $batches = [int][Math]::Ceiling($targetRows / $RowsPerInsertBatch)
    for ($batch = 0; $batch -lt $batches; $batch++) {
        $offset = [int64]$batch * $RowsPerInsertBatch
        $count = [Math]::Min($RowsPerInsertBatch, $targetRows - $offset)
        Write-Host "Loading large table batch $($batch + 1) of $batches ($count rows per large table)."
        $seedLargeSql = @"
INSERT INTO $DatabaseName.dist_ontime
SELECT
    toDate('2015-01-01') + (number % 730),
    toYear(toDate('2015-01-01') + (number % 730)),
    toMonth(toDate('2015-01-01') + (number % 730)),
    toDayOfMonth(toDate('2015-01-01') + (number % 730)),
    concat('AIR', toString(number % 32)),
    concat('O', leftPad(toString(number % 500), 3, '0')),
    concat('D', leftPad(toString((number * 7) % 500), 3, '0')),
    toUInt32(number % 100000),
    toInt16((number % 240) - 120),
    toInt16(((number * 3) % 240) - 120),
    toUInt16(100 + (number % 4500)),
    repeat(concat('ontime-demo-', toString(number), '-'), 20)
FROM numbers($offset, $count);

INSERT INTO $DatabaseName.dist_ontime_events
SELECT
    toDateTime('2015-01-01 00:00:00') + toIntervalSecond(number % 63072000),
    toDate(toDateTime('2015-01-01 00:00:00') + toIntervalSecond(number % 63072000)),
    concat('AIR', toString(number % 32)),
    concat('A', leftPad(toString(number % 500), 3, '0')),
    concat('event_', toString(number % 20)),
    toUInt32(number % 1000000),
    toUInt32((number * 13) % 1000000),
    sin(number),
    cos(number),
    repeat(concat('event-demo-', toString(number), '-'), 20)
FROM numbers($offset, $count);
"@
        Invoke-ClickHouse 'clickhouse-s1-r1' $seedLargeSql | Out-Null
    }

    foreach ($node in $ClickHouseNodes) {
        foreach ($table in $LargeLocalTables) {
            Invoke-ClickHouse $node "SYSTEM SYNC REPLICA $DatabaseName.$table" | Out-Null
        }
    }
}

$existingTargets = @(ConvertFrom-CommandJson (Invoke-ChoboCli (@('targets', 'list') + $CommonCliArgs)))
$targetJson = $existingTargets | Where-Object { $_.name -eq 'demo-minio' } | Select-Object -First 1
if (-not $targetJson) {
    $targetJson = ConvertFrom-CommandJson (Invoke-ChoboCli (@('targets', 'add-s3', '--name', 'demo-minio', '--endpoint', $MinioInternalEndpoint, '--bucket', $Bucket, '--access-key', $MinioAccessKey, '--secret-key', $MinioSecretKey, '--force-path-style') + $CommonCliArgs))
}

$existingClusters = @(ConvertFrom-CommandJson (Invoke-ChoboCli (@('clusters', 'list') + $CommonCliArgs)))
$clusterJson = $existingClusters | Where-Object { $_.name -eq 'demo-clickhouse-cluster' } | Select-Object -First 1
if (-not $clusterJson) {
    $clusterJson = ConvertFrom-CommandJson (Invoke-ChoboCli (@('clusters', 'add', '--name', 'demo-clickhouse-cluster', '--mode', 'Cluster', '--node', 'clickhouse-s1-r1:9000', '--clickhouse-cluster-name', $ClusterName, '--backup-restore-maxdop', '2') + $CommonCliArgs))
}

$verification = [ordered]@{
    HostPublishedAccess = @()
    ChoboConnectivity = @()
    ClickHouseLocalTables = @()
    ClickHouseDistributedTables = @()
}
$authHeaders = @{ Authorization = "Bearer $AccessToken" }
$verification.HostPublishedAccess += Assert-HttpReachable 'Chobo Web GUI through published port' "$ChoboPublicUrl/"
$verification.HostPublishedAccess += Assert-HttpReachable 'Chobo API through published port' "$ChoboPublicUrl/api/v1/server/version" $authHeaders
$verification.HostPublishedAccess += Assert-HttpReachable 'MinIO S3 API through published port' "$MinioPublicEndpoint/minio/health/ready"
$verification.HostPublishedAccess += Assert-HttpReachable 'MinIO Console through published port' "$MinioConsoleUrl/"

Invoke-ChoboCli (@('targets', 'test-connection', '--id', $targetJson.id) + $CommonCliArgs) | Out-Null
$verification.ChoboConnectivity += [ordered]@{ Name = 'Chobo can access MinIO S3 target'; TargetId = $targetJson.id; Status = 'Passed' }
Invoke-ChoboCli (@('clusters', 'test-connection', '--id', $clusterJson.id) + $CommonCliArgs) | Out-Null
$verification.ChoboConnectivity += [ordered]@{ Name = 'Chobo can access ClickHouse cluster'; ClusterId = $clusterJson.id; Status = 'Passed' }

foreach ($node in $ClickHouseNodes) {
    foreach ($table in $LargeLocalTables) {
        $exists = Invoke-ClickHouse $node "EXISTS TABLE $DatabaseName.$table"
        if ($exists -ne '1') { throw "$DatabaseName.$table is missing on $node." }
        if ($SkipSeed) {
            $verification.ClickHouseLocalTables += [ordered]@{ Node = $node; Table = "$DatabaseName.$table"; Status = 'Exists'; Rows = $null }
        } else {
            $verification.ClickHouseLocalTables += Assert-PositiveCount $node "$DatabaseName.$table"
        }
    }
    foreach ($table in $LargeDistributedTables) {
        $exists = Invoke-ClickHouse $node "EXISTS TABLE $DatabaseName.$table"
        if ($exists -ne '1') { throw "$DatabaseName.$table is missing on $node." }
        if ($SkipSeed) {
            $verification.ClickHouseDistributedTables += [ordered]@{ Node = $node; Table = "$DatabaseName.$table"; Status = 'Exists'; Rows = $null }
        } else {
            $verification.ClickHouseDistributedTables += Assert-PositiveCount $node "$DatabaseName.$table"
        }
    }
}

$summary = [ordered]@{
    Chobo = [ordered]@{ WebUrl = $ChoboPublicUrl; ApiUrl = $ChoboPublicUrl; AdminUser = 'admin'; AccessToken = $AccessToken }
    MinIO = [ordered]@{ ConsoleUrl = $MinioConsoleUrl; HostS3Endpoint = $MinioPublicEndpoint; ContainerS3Endpoint = $MinioInternalEndpoint; Bucket = $Bucket; AccessKey = $MinioAccessKey; SecretKey = $MinioSecretKey }
    ClickHouse = [ordered]@{
        ClusterName = $ClusterName
        Database = $DatabaseName
        User = 'default'
        Password = ''
        Nodes = @(
            [ordered]@{ Name = 'clickhouse-s1-r1'; Shard = 1; Replica = 1; HttpUrl = 'http://localhost:18111'; NativePort = 19111 },
            [ordered]@{ Name = 'clickhouse-s1-r2'; Shard = 1; Replica = 2; HttpUrl = 'http://localhost:18112'; NativePort = 19112 },
            [ordered]@{ Name = 'clickhouse-s2-r1'; Shard = 2; Replica = 1; HttpUrl = 'http://localhost:18121'; NativePort = 19121 },
            [ordered]@{ Name = 'clickhouse-s2-r2'; Shard = 2; Replica = 2; HttpUrl = 'http://localhost:18122'; NativePort = 19122 }
        )
        LargeLocalTables = $LargeLocalTables
        LargeDistributedTables = $LargeDistributedTables
        SmallLocalTablePattern = 'local_small_1..local_small_10'
        SmallDistributedTablePattern = 'dist_small_1..dist_small_10'
        BigTableTargetGb = $BigTableTargetGb
    }
    ChoboRegistration = [ordered]@{ StorageTargetName = 'demo-minio'; StorageTargetId = $targetJson.id; ClusterName = 'demo-clickhouse-cluster'; ClusterId = $clusterJson.id }
    Verification = $verification
}
$summaryPath = Join-Path $OutputDirectory 'demo-env.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host 'Chobo demo initialization succeeded.'
$summary | ConvertTo-Json -Depth 8