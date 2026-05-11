function Invoke-ChoboCli {
    param(
        [Parameter(ValueFromRemainingArguments)] [string[]]$Arguments
    )

    $result = Invoke-ChoboCliCommand -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw "ChoboCli failed with exit code $($result.ExitCode). Output: $($result.OutputText)"
    }

    $result.OutputLines
}

function Invoke-ChoboCliCommand {
    param(
        [string[]]$Arguments = @()
    )

    $cliPath = Get-Command ChoboCli -ErrorAction SilentlyContinue
    if (-not $cliPath) {
        throw 'ChoboCli is not available in the test-runner container yet.'
    }

    $output = & ChoboCli @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { [string]$_ })
    $text = $lines -join [Environment]::NewLine
    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try {
            $json = $text | ConvertFrom-Json
        } catch {
            $json = $null
        }
    }

    [pscustomobject]@{
        Arguments = @($Arguments)
        ExitCode = $exitCode
        OutputLines = $lines
        OutputText = $text
        Json = $json
    }
}

function Split-ChoboCliCommandLine {
    param(
        [Parameter(Mandatory)] [string]$Command
    )

    [System.Management.Automation.PSParser]::Tokenize($Command, [ref]$null) |
        Where-Object { $_.Type -eq 'CommandArgument' -or $_.Type -eq 'Command' } |
        ForEach-Object { $_.Content }
}

Export-ModuleMember -Function Invoke-ChoboCli, Invoke-ChoboCliCommand, Split-ChoboCliCommandLine
