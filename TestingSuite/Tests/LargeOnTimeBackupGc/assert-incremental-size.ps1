param(
    [Parameter(Mandatory)] [string]$FullBackupId,
    [Parameter(Mandatory)] [string]$IncrementalBackupId,
    [long]$MaxIncrementalBytes = 268435456,
    [double]$MaxIncrementalRatio = 0.01
)

$headers = @{ Authorization = 'Bearer static-test-token' }
$full = Invoke-RestMethod -Uri "http://choboserver:8080/api/v1/backups/$FullBackupId" -Headers $headers
$incremental = Invoke-RestMethod -Uri "http://choboserver:8080/api/v1/backups/$IncrementalBackupId" -Headers $headers

if ($full.status -ne 'Succeeded') {
    throw "Full backup $FullBackupId status was $($full.status)."
}
if ($incremental.status -ne 'Succeeded') {
    throw "Incremental backup $IncrementalBackupId status was $($incremental.status)."
}
if ($full.backupSizeBytes -le 0) {
    throw "Full backup size was not recorded. Value: $($full.backupSizeBytes)."
}
if ($incremental.backupSizeBytes -lt 0) {
    throw "Incremental backup size was invalid. Value: $($incremental.backupSizeBytes)."
}

$ratio = [double]$incremental.backupSizeBytes / [double]$full.backupSizeBytes
Write-Host "fullBackupSizeBytes=$($full.backupSizeBytes)"
Write-Host "incrementalBackupSizeBytes=$($incremental.backupSizeBytes)"
Write-Host "incrementalRatio=$ratio"

if ($incremental.backupSizeBytes -gt $MaxIncrementalBytes) {
    throw "No-op incremental backup is too large: $($incremental.backupSizeBytes) bytes > $MaxIncrementalBytes bytes."
}
if ($ratio -gt $MaxIncrementalRatio) {
    throw "No-op incremental backup ratio is too large: $ratio > $MaxIncrementalRatio."
}
