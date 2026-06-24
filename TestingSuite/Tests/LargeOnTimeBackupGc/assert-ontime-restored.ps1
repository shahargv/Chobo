param(
    [Parameter(Mandatory)] [string]$SourceHost,
    [Parameter(Mandatory)] [string]$RestoreHost
)

function Invoke-ClickHouseScalarRow {
    param(
        [Parameter(Mandatory)] [string]$HostName,
        [Parameter(Mandatory)] [string]$Query
    )

    $output = & clickhouse-client --host $HostName --query $Query 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "clickhouse-client failed on $HostName with exit code $LASTEXITCODE. Output: $output"
    }

    ($output | Out-String).Trim()
}

$query = "SELECT count(), min(FlightDate), max(FlightDate), sum(cityHash64(Year, Month, DayofMonth, FlightDate, Reporting_Airline, Flight_Number_Reporting_Airline, Origin, Dest)) FROM large_ontime_source.ontime FORMAT TSV"
$source = Invoke-ClickHouseScalarRow -HostName $SourceHost -Query $query
$restored = Invoke-ClickHouseScalarRow -HostName $RestoreHost -Query $query

Write-Host "source=$source"
Write-Host "restored=$restored"

if ($source -ne $restored) {
    throw "Restored OnTime table summary did not match source. Source: $source Restored: $restored"
}
