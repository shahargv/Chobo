function ConvertTo-ChoboHtml {
    param(
        [Parameter(Mandatory)] $RunSummary
    )

    $rows = foreach ($result in $RunSummary.Results) {
        $statusClass = if ($result.Status -eq 'Passed') { 'passed' } else { 'failed' }
        $error = [System.Net.WebUtility]::HtmlEncode($result.Error)
        $artifact = [System.Net.WebUtility]::HtmlEncode($result.ArtifactDirectory)
        "<tr class='$statusClass'><td>$([System.Net.WebUtility]::HtmlEncode($result.Test))</td><td>$($result.Status)</td><td>$($result.DurationSeconds)</td><td>$($result.TimedOut)</td><td><code>$artifact</code></td><td><pre>$error</pre></td></tr>"
    }

    @"
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <title>Chobo System Test Results - $($RunSummary.RunId)</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; margin: 24px; color: #1f2933; }
    table { border-collapse: collapse; width: 100%; }
    th, td { border: 1px solid #d7dde5; padding: 8px; vertical-align: top; }
    th { background: #f3f6f9; text-align: left; }
    tr.passed td:first-child { border-left: 6px solid #2f9e44; }
    tr.failed td:first-child { border-left: 6px solid #d9480f; }
    code, pre { white-space: pre-wrap; }
    .summary { display: flex; gap: 16px; margin: 16px 0; }
    .summary div { border: 1px solid #d7dde5; padding: 10px 14px; border-radius: 6px; }
  </style>
</head>
<body>
  <h1>Chobo System Test Results</h1>
  <p><strong>Run:</strong> $($RunSummary.RunId)</p>
  <div class="summary">
    <div>Total: $($RunSummary.Total)</div>
    <div>Passed: $($RunSummary.Passed)</div>
    <div>Failed: $($RunSummary.Failed)</div>
    <div>Duration: $($RunSummary.DurationSeconds)s</div>
  </div>
  <table>
    <thead>
      <tr><th>Test</th><th>Status</th><th>Seconds</th><th>Timed out</th><th>Artifacts</th><th>Error</th></tr>
    </thead>
    <tbody>
      $($rows -join [Environment]::NewLine)
    </tbody>
  </table>
</body>
</html>
"@
}

function Write-ChoboReports {
    param(
        [Parameter(Mandatory)] $RunSummary,
        [Parameter(Mandatory)] [string]$OutputDirectory
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $RunSummary | ConvertTo-Json -Depth 20 | Set-Content -Path (Join-Path $OutputDirectory 'results.json')
    ConvertTo-ChoboHtml -RunSummary $RunSummary | Set-Content -Path (Join-Path $OutputDirectory 'index.html')
}

Export-ModuleMember -Function Write-ChoboReports
