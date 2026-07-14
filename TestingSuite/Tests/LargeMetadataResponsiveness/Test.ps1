function Get-ChoboTestDefinition {
    @{
        Name = 'LargeMetadataResponsiveness'
        Description = 'Seeds hundreds of backup records over thousands of tables and shards, then measures user-facing and background-operation responsiveness.'
        TimeoutSeconds = 420
        ExcludeFromRunAll = $true
        Resources = @(
            @{
                Name = 'server'
                Type = 'ChoboServer'
                Environment = @{
                    Chobo__RetentionManagement__Interval = '00:00:01'
                    Chobo__BackupsGarbageCollector__Interval = '00:00:01'
                    Chobo__SqliteSelfBackup__Enabled = 'false'
                }
            }
        )
        Setup = {
            param($Context)
            Wait-ChoboServerApi -TimeoutSeconds 120
        }
        Action = {
            param($Context)
            Invoke-LargeMetadataResponsiveness -Context $Context
        }
        Verify = {
            param($Context)
            Assert-LargeMetadataResponsiveness -Context $Context
        }
    }
}

function New-ChoboHttpClient {
    $client = [System.Net.Http.HttpClient]::new()
    $client.BaseAddress = [Uri]'http://choboserver:8080/api/v1/'
    $client.Timeout = [TimeSpan]::FromSeconds(300)
    $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'static-test-token')
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

function Invoke-TimedHttp {
    param(
        [Parameter(Mandatory)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Method,
        [Parameter(Mandatory)] [string]$Path,
        [object]$Body,
        [int]$MaxMs
    )

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Path)
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 20 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $sw.Stop()
    $response.Dispose()
    $request.Dispose()

    if (-not $response.IsSuccessStatusCode) {
        throw "$Name returned HTTP $([int]$response.StatusCode): $content"
    }

    $jsonResult = $null
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try { $jsonResult = $content | ConvertFrom-Json } catch { $jsonResult = $null }
    }

    $row = [pscustomobject]@{
        name = $Name
        kind = 'http'
        method = $Method
        path = $Path
        elapsedMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        maxMs = $MaxMs
        bytes = [System.Text.Encoding]::UTF8.GetByteCount($content)
        ok = $sw.Elapsed.TotalMilliseconds -le $MaxMs
    }
    $row | Add-Member -NotePropertyName parsedJson -NotePropertyValue $jsonResult -Force
    $row
}

function Invoke-TimedCli {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [int]$MaxMs
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-ChoboCliCommand -Arguments ($Arguments + @('--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token'))
    $sw.Stop()
    if ($result.ExitCode -ne 0) {
        throw "$Name failed with exit code $($result.ExitCode): $($result.OutputText)"
    }

    [pscustomobject]@{
        name = $Name
        kind = 'cli'
        command = ($Arguments -join ' ')
        elapsedMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        maxMs = $MaxMs
        bytes = [System.Text.Encoding]::UTF8.GetByteCount($result.OutputText)
        ok = $sw.Elapsed.TotalMilliseconds -le $MaxMs
    }
}

function Invoke-LargeMetadataResponsiveness {
    param($Context)

    $timings = New-Object System.Collections.Generic.List[object]
    $client = New-ChoboHttpClient
    try {
        $seed = Invoke-TimedHttp -Client $client -Name 'seed large metadata graph' -Method 'POST' -Path 'test-hooks/seed-large-metadata-graph' -Body @{
            backupCount = 300
            tablesPerBackup = 100
            shardsPerTable = 24
            restoreCount = 20
            completedQueueRows = 1000
        } -MaxMs 240000
        $timings.Add($seed)
        $seedJson = $seed.parsedJson
        if ($seedJson.backupCount -lt 300 -or $seedJson.incrementalBackupCount -lt 270 -or $seedJson.backupTableCount -lt 30000 -or $seedJson.backupShardCount -lt 720000) {
            throw "Seeded graph was smaller than expected: $($seedJson | ConvertTo-Json -Compress)"
        }

        Start-Sleep -Seconds 4

        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api backups summary list' -Method 'GET' -Path 'backups?includeTables=false' -MaxMs 5000))
        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api backup summary show' -Method 'GET' -Path "backups/$($seedJson.sampleBackupId)?includeTables=false" -MaxMs 2000))
        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api backup detailed show' -Method 'GET' -Path "backups/$($seedJson.sampleBackupId)" -MaxMs 5000))
        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api dashboard' -Method 'GET' -Path 'dashboard?nextHours=24' -MaxMs 5000))
        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api queue all' -Method 'GET' -Path 'queue?status=all&limit=1000' -MaxMs 5000))
        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api audit recent' -Method 'GET' -Path 'audit?last=50' -MaxMs 3000))
        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api logs recent' -Method 'GET' -Path 'logs?last=50' -MaxMs 3000))
        $timings.Add((Invoke-TimedHttp -Client $client -Name 'api garbage collector run' -Method 'POST' -Path 'backups/garbage-collector/run' -Body @{} -MaxMs 5000))

        $timings.Add((Invoke-TimedCli -Name 'cli dashboard show' -Arguments @('dashboard', 'show', '--next-hours', '24') -MaxMs 8000))
        $timings.Add((Invoke-TimedCli -Name 'cli queue list all' -Arguments @('queue', 'list', '--status', 'all') -MaxMs 8000))
        $timings.Add((Invoke-TimedCli -Name 'cli audit show' -Arguments @('audit', 'show', '--last', '50') -MaxMs 5000))
        $timings.Add((Invoke-TimedCli -Name 'cli logs show' -Arguments @('logs', 'show', '--last', '50') -MaxMs 5000))
    } finally {
        $client.Dispose()
    }

    $timings | Select-Object name,kind,method,path,command,elapsedMs,maxMs,bytes,ok | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $Context.OutputDirectory 'timings.json')
    $timings | Sort-Object elapsedMs -Descending | Format-Table name,kind,elapsedMs,maxMs,bytes,ok -AutoSize | Out-String | Set-Content -Path (Join-Path $Context.OutputDirectory 'timings.txt')
}

function Assert-LargeMetadataResponsiveness {
    param($Context)

    $timingsPath = Join-Path $Context.OutputDirectory 'timings.json'
    if (-not (Test-Path $timingsPath)) {
        throw 'timings.json was not created.'
    }

    $timings = @(Get-Content -Raw -Path $timingsPath | ConvertFrom-Json)
    $slow = @($timings | Where-Object { -not $_.ok })
    if ($slow.Count -gt 0) {
        $summary = ($slow | Sort-Object elapsedMs -Descending | ForEach-Object { "$($_.name) $($_.elapsedMs)ms > $($_.maxMs)ms bytes=$($_.bytes)" }) -join '; '
        throw "Large metadata responsiveness thresholds exceeded: $summary"
    }

    $audit = Invoke-ChoboCliCommand -Arguments @('audit', 'show', '--last', '50', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
    if ($audit.ExitCode -ne 0 -or -not $audit.OutputText.Contains('large-metadata-seeded')) {
        throw 'large-metadata-seeded audit record was not visible.'
    }

    $logs = Invoke-ChoboCliCommand -Arguments @('logs', 'show', '--last', '500', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
    if ($logs.ExitCode -ne 0 -or -not $logs.OutputText.Contains('Seeded large metadata graph')) {
        throw 'large metadata seed log record was not visible.'
    }
}
