$script:SlowQueryGateMs = 100
$script:ServerUrl = 'http://choboserver:8080'
$script:AccessToken = 'static-test-token'
$script:SlowQueryCategory = 'ChoboServer.Data.SlowSqliteQueryLoggingInterceptor'
# Parked high while seeding and while reading results back, so neither the seed transaction nor the
# log read itself lands in the window we are measuring.
$script:QuietThreshold = '00:05:00'

function Get-ChoboTestDefinition {
    @{
        Name = 'SqlitePerformanceUnderLoad'
        Description = 'Replays every GUI API flow against a large seeded metadata graph with the SQLite slow-query threshold lowered, and fails if any single query exceeds the gate.'
        TimeoutSeconds = 1800
        ExcludeFromRunAll = $true
        Resources = @(
            @{
                Name = 'server'
                Type = 'ChoboServer'
                Environment = @{
                    # Background sweeps would otherwise run their own queries inside the measured
                    # window and be attributed to whichever endpoint happened to be in flight.
                    Chobo__RetentionManagement__Interval = '12:00:00'
                    Chobo__BackupsGarbageCollector__Interval = '12:00:00'
                    Chobo__SqliteSelfBackup__Enabled = 'false'
                    # Seeding awaits one statistics refresh. Park the periodic worker so it cannot
                    # race the measured replay and make unrelated reads wait behind PRAGMA optimize.
                    Chobo__Sqlite__QueryStatisticsRefreshInterval = '12:00:00'
                    Chobo__DatabaseLogging__SlowQueryThreshold = '00:05:00'
                    # Measure the heaviest supported configuration, not the cheap default:
                    # per-table-shard metrics are opt-in, and they are what makes /metrics
                    # read the largest table in the database.
                    Chobo__Metrics__IncludeTableShardMetrics = 'true'
                }
            }
        )
        Setup = {
            param($Context)
            Wait-ChoboServerApi -TimeoutSeconds 120
        }
        Action = {
            param($Context)
            Invoke-SqlitePerformanceUnderLoad -Context $Context
        }
        Verify = {
            param($Context)
            Assert-SqlitePerformanceUnderLoad -Context $Context
        }
    }
}

function New-ChoboHttpClient {
    $client = [System.Net.Http.HttpClient]::new()
    $client.BaseAddress = [Uri]"$script:ServerUrl/api/v1/"
    $client.Timeout = [TimeSpan]::FromSeconds(600)
    $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $script:AccessToken)
    $client
}

function Wait-ChoboServerApi {
    param([int]$TimeoutSeconds = 120)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $client = New-ChoboHttpClient
        try {
            $response = $client.GetAsync('users').GetAwaiter().GetResult()
            if ($response.IsSuccessStatusCode) {
                return
            }
        } catch {
        } finally {
            $client.Dispose()
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    throw "ChoboServer API did not become ready within $TimeoutSeconds seconds."
}

function Invoke-ChoboApi {
    param(
        [Parameter(Mandatory)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)] [string]$Method,
        [Parameter(Mandatory)] [string]$Path,
        [object]$Body,
        [switch]$AllowFailure,
        [switch]$Stream
    )

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Path)
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 20 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # HttpCompletionOption.ResponseHeadersRead keeps the body out of memory until we choose how to
    # drain it - data/export is a nine-figure byte count at this scale and must never be
    # materialised as a PowerShell string, let alone parsed.
    $response = $Client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $ok = $response.IsSuccessStatusCode
    $status = [int]$response.StatusCode

    $content = $null
    $byteCount = 0L
    if ($Stream -and $ok) {
        $body = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $buffer = [byte[]]::new(131072)
            while ($true) {
                $read = $body.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { break }
                $byteCount += $read
            }
        } finally {
            $body.Dispose()
        }
    } else {
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $byteCount = [System.Text.Encoding]::UTF8.GetByteCount($content)
    }
    $sw.Stop()
    $response.Dispose()
    $request.Dispose()

    if (-not $ok -and -not $AllowFailure) {
        throw "$Method $Path returned HTTP ${status}: $content"
    }

    $parsed = $null
    if (-not $Stream -and -not [string]::IsNullOrWhiteSpace($content)) {
        try { $parsed = $content | ConvertFrom-Json } catch { $parsed = $null }
    }

    [pscustomobject]@{
        method = $Method
        path = $Path
        status = $status
        ok = $ok
        elapsedMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        bytes = $byteCount
        json = $parsed
    }
}

function Set-SlowQueryThreshold {
    param(
        [Parameter(Mandatory)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)] [string]$Value
    )

    # The setting is applied live, so no restart is needed between passes. The key is escaped the
    # same way the CLI escapes it, because the route is a catch-all and the key contains colons.
    $key = [Uri]::EscapeDataString('Chobo:DatabaseLogging:SlowQueryThreshold')
    $null = Invoke-ChoboApi -Client $Client -Method 'PUT' -Path "settings/$key" -Body @{ value = $Value }
}

function Get-GuiFlowRequests {
    param([Parameter(Mandatory)] $Seed, [Parameter(Mandatory)] $Ids)

    $backupId = $Seed.sampleBackupId
    $from14d = (Get-Date).ToUniversalTime().AddDays(-14).ToString('o')
    $from7d = (Get-Date).ToUniversalTime().AddDays(-7).ToString('o')
    $from1h = (Get-Date).ToUniversalTime().AddHours(-1).ToString('o')
    $now = (Get-Date).ToUniversalTime().ToString('o')

    $requests = New-Object System.Collections.Generic.List[object]
    function Add-Request {
        param($Name, $Method, $Path, $Body)
        $requests.Add([pscustomobject]@{ name = $Name; method = $Method; path = $Path; body = $Body })
    }

    # AppShell - issued on every page load.
    Add-Request 'appshell: server version' 'GET' 'server/version' $null
    Add-Request 'appshell: install status' 'GET' 'server/install/status' $null

    # Dashboard page.
    Add-Request 'dashboard: summary' 'GET' 'dashboard?nextHours=6' $null
    Add-Request 'dashboard: missing backups' 'GET' 'dashboard/missing-backups?hours=24' $null
    Add-Request 'dashboard: backups list' 'GET' 'backups?includeTables=false' $null
    Add-Request 'dashboard: restores list' 'GET' 'restores' $null
    Add-Request 'dashboard: policies' 'GET' 'policies' $null
    Add-Request 'dashboard: schedules' 'GET' 'schedules' $null
    Add-Request 'dashboard: clusters' 'GET' 'clusters' $null
    Add-Request 'dashboard: targets' 'GET' 'targets' $null

    # Monitoring page - the unbounded shard aggregation.
    Add-Request 'monitoring: metrics' 'GET' 'metrics' $null

    # Backups page, default filter is a 14 day window.
    Add-Request 'backups: filtered list' 'GET' "backups?from=$from14d&includeTables=false" $null
    Add-Request 'backups: drawer summary' 'GET' "backups/$backupId`?includeTables=false" $null
    Add-Request 'backups: drawer tables' 'GET' "backups/$backupId`?includeTables=true" $null
    Add-Request 'backups: drawer logs' 'GET' "logs?operationId=$backupId&last=500" $null
    Add-Request 'backups: drawer audit' 'GET' "audit?operationId=$backupId&last=500" $null
    Add-Request 'backups: gc evaluation' 'GET' "backups/$backupId/garbage-collection-evaluation" $null
    Add-Request 'backups: settings preview' 'POST' 'backups/settings-preview' @{ clusterId = $Seed.clusterId; policyId = $Seed.policyId }

    # Garbage collector page.
    Add-Request 'gc: status' 'GET' 'backups/garbage-collector/status' $null
    Add-Request 'gc: queue' 'GET' 'backups/garbage-collector/queue' $null
    Add-Request 'gc: logs' 'GET' "logs?startTime=$from1h&endTime=$now&limit=500" $null
    Add-Request 'gc: audit' 'GET' "audit?startTime=$from1h&endTime=$now&limit=500" $null

    # Schema browser, default preset is the last 7 days.
    Add-Request 'schema: backups list' 'GET' "schema/backups?from=$from7d&to=$now" $null
    Add-Request 'schema: backup detail' 'GET' "schema/backups/$backupId" $null
    Add-Request 'schema: export' 'GET' "schema/backups/$backupId/export" $null

    # Queue page.
    Add-Request 'queue: active' 'GET' 'queue?kind=All&status=active' $null
    Add-Request 'queue: all' 'GET' 'queue?status=all&limit=1000' $null

    # Logs and Audit pages.
    Add-Request 'logs: page' 'GET' "logs?startTime=$from1h&endTime=$now&offset=0&limit=200" $null
    Add-Request 'audit: page' 'GET' "audit?startTime=$from1h&endTime=$now&offset=0&limit=200" $null

    # Users, Settings.
    Add-Request 'users: list' 'GET' 'users' $null
    Add-Request 'settings: list' 'GET' 'settings' $null

    if ($Ids.UserId) {
        Add-Request 'users: tokens' 'GET' "users/$($Ids.UserId)/tokens" $null
    }

    # Restore history and detail. RestoreDetail polls logs and audit at limit=10000.
    if ($Ids.RestoreId) {
        Add-Request 'restores: detail' 'GET' "restores/$($Ids.RestoreId)" $null
        Add-Request 'restores: detail logs' 'GET' "logs?operationId=$($Ids.RestoreId)&limit=10000" $null
        Add-Request 'restores: detail audit' 'GET' "audit?operationId=$($Ids.RestoreId)&limit=10000" $null
        Add-Request 'restores: settings preview' 'POST' 'restores/settings-preview' @{ backupId = $backupId; targetClusterId = $Seed.clusterId }
    }

    # Import/export page. The data export is the widest read in the product.
    Add-Request 'importexport: config export' 'GET' 'config/export' $null
    Add-Request 'importexport: data export' 'GET' 'data/export' $null

    $requests
}

function Invoke-ReplayPass {
    param(
        [Parameter(Mandatory)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)] [string]$PassName,
        [Parameter(Mandatory)] [string]$ThresholdValue,
        [Parameter(Mandatory)] $Requests
    )

    Set-SlowQueryThreshold -Client $Client -Value $ThresholdValue
    # The threshold is read per command from IOptionsMonitor over a reloadOnChange file source;
    # give the file watcher a moment so the first replayed request is already at the new value.
    Start-Sleep -Seconds 3

    $startedAt = (Get-Date).ToUniversalTime().AddSeconds(-1)
    $timings = New-Object System.Collections.Generic.List[object]
    foreach ($request in $Requests) {
        $result = Invoke-ChoboApi -Client $Client -Method $request.method -Path $request.path -Body $request.body -AllowFailure -Stream
        $timings.Add([pscustomobject]@{
            pass = $PassName
            name = $request.name
            method = $request.method
            path = $request.path
            status = $result.status
            ok = $result.ok
            elapsedMs = $result.elapsedMs
            bytes = $result.bytes
        })
    }
    $endedAt = (Get-Date).ToUniversalTime().AddSeconds(1)

    # Raise the threshold again before reading results back, so the log read cannot append to the
    # very window it is reporting on.
    Set-SlowQueryThreshold -Client $Client -Value $script:QuietThreshold
    Start-Sleep -Seconds 3

    $slow = Get-SlowQueryEntries -Client $Client -StartedAt $startedAt -EndedAt $endedAt

    [pscustomobject]@{
        pass = $PassName
        threshold = $ThresholdValue
        timings = $timings
        slowQueries = $slow
    }
}

function Get-SlowQueryEntries {
    param(
        [Parameter(Mandatory)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)] [datetime]$StartedAt,
        [Parameter(Mandatory)] [datetime]$EndedAt
    )

    $entries = New-Object System.Collections.Generic.List[object]
    $offset = 0
    $limit = 10000
    while ($true) {
        $path = "logs?startTime=$($StartedAt.ToString('o'))&endTime=$($EndedAt.ToString('o'))&offset=$offset&limit=$limit"
        $page = Invoke-ChoboApi -Client $Client -Method 'GET' -Path $path
        $items = @($page.json.items)
        foreach ($item in $items) {
            if ($item.category -ne $script:SlowQueryCategory) { continue }
            $match = [regex]::Match($item.message, 'completed in ([0-9]+(?:\.[0-9]+)?) ms')
            $elapsed = if ($match.Success) { [double]$match.Groups[1].Value } else { -1 }
            $sqlMatch = [regex]::Match($item.message, 'CommandText=(.*)$', 'Singleline')
            $sql = if ($sqlMatch.Success) { $sqlMatch.Groups[1].Value } else { $item.message }
            $entries.Add([pscustomobject]@{
                timestamp = $item.timestamp
                elapsedMs = $elapsed
                sql = ($sql -replace '\s+', ' ').Trim()
            })
        }
        if ($items.Count -lt $limit) { break }
        $offset += $limit
        if ($offset -ge 100000) { break }
    }

    $entries
}

function Invoke-SqlitePerformanceUnderLoad {
    param($Context)

    $client = New-ChoboHttpClient
    try {
        $seedResponse = Invoke-ChoboApi -Client $client -Method 'POST' -Path 'test-hooks/seed-large-metadata-graph' -Body @{
            backupCount = 300
            tablesPerBackup = 100
            shardsPerTable = 24
            restoreCount = 20
            completedQueueRows = 1000
        }
        $seed = $seedResponse.json
        if ($seed.backupTableCount -lt 30000 -or $seed.backupShardCount -lt 720000) {
            throw "Seeded graph was smaller than expected: $($seed | ConvertTo-Json -Compress)"
        }
        # Without real parent-table and parent-shard links the incremental-chain and GC dependency
        # queries match nothing, and the fixture silently fails to exercise them at all.
        if ($seed.parentTableLinkCount -le 0 -or $seed.parentShardLinkCount -le 0) {
            throw "Seeded graph has no incremental parent links: $($seed | ConvertTo-Json -Compress)"
        }

        $ids = @{ UserId = $null; RestoreId = $null }
        $users = Invoke-ChoboApi -Client $client -Method 'GET' -Path 'users'
        if (@($users.json).Count -gt 0) { $ids.UserId = @($users.json)[0].id }
        $restores = Invoke-ChoboApi -Client $client -Method 'GET' -Path 'restores'
        if (@($restores.json).Count -gt 0) { $ids.RestoreId = @($restores.json)[0].id }

        $requests = Get-GuiFlowRequests -Seed $seed -Ids $ids

        # Pass 1 ranks the whole tail so the fixes that follow are chosen from data.
        $diagnostic = Invoke-ReplayPass -Client $client -PassName 'diagnostic' -ThresholdValue '00:00:00.001' -Requests $requests
        # Pass 2 is the gate.
        $gate = Invoke-ReplayPass -Client $client -PassName 'gate' -ThresholdValue ([TimeSpan]::FromMilliseconds($script:SlowQueryGateMs).ToString('c')) -Requests $requests

        $summary = [pscustomobject]@{
            gateMs = $script:SlowQueryGateMs
            seed = $seed
            diagnostic = $diagnostic
            gate = $gate
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $Context.OutputDirectory 'sqlite-performance.json')

        $topDiagnostic = @($diagnostic.slowQueries | Sort-Object elapsedMs -Descending | Select-Object -First 40)
        $report = New-Object System.Collections.Generic.List[string]
        $report.Add("Gate: $($script:SlowQueryGateMs) ms")
        $report.Add("Diagnostic pass captured $(@($diagnostic.slowQueries).Count) queries above 1 ms.")
        $report.Add("Gate pass reported $(@($gate.slowQueries).Count) queries above the gate.")
        $report.Add('')
        $report.Add('--- slowest queries (diagnostic pass) ---')
        foreach ($entry in $topDiagnostic) {
            $report.Add(("{0,9:N1} ms  {1}" -f $entry.elapsedMs, $entry.sql.Substring(0, [Math]::Min(400, $entry.sql.Length))))
        }
        $report.Add('')
        $report.Add('--- endpoint wall clock (gate pass) ---')
        foreach ($entry in @($gate.timings | Sort-Object elapsedMs -Descending)) {
            $report.Add(("{0,9:N1} ms  {1,-40} status={2} bytes={3}" -f $entry.elapsedMs, $entry.name, $entry.status, $entry.bytes))
        }
        $report -join [Environment]::NewLine | Set-Content -Path (Join-Path $Context.OutputDirectory 'sqlite-performance.txt')
    } finally {
        $client.Dispose()
    }
}

function Assert-SqlitePerformanceUnderLoad {
    param($Context)

    $path = Join-Path $Context.OutputDirectory 'sqlite-performance.json'
    if (-not (Test-Path $path)) {
        throw 'sqlite-performance.json was not created.'
    }

    $summary = Get-Content -Raw -Path $path | ConvertFrom-Json

    $failedRequests = @($summary.gate.timings | Where-Object { -not $_.ok })
    if ($failedRequests.Count -gt 0) {
        $detail = ($failedRequests | ForEach-Object { "$($_.name) -> HTTP $($_.status)" }) -join '; '
        throw "GUI flow replay had failing requests: $detail"
    }

    $slow = @($summary.gate.slowQueries | Sort-Object elapsedMs -Descending)
    if ($slow.Count -gt 0) {
        $detail = ($slow | Select-Object -First 10 | ForEach-Object {
            "{0:N1} ms: {1}" -f $_.elapsedMs, $_.sql.Substring(0, [Math]::Min(240, $_.sql.Length))
        }) -join ' | '
        throw "$($slow.Count) SQLite queries exceeded the $($summary.gateMs) ms gate under load: $detail"
    }
}
