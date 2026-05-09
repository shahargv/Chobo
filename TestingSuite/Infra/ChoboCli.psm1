function Invoke-ChoboCli {
    param(
        [Parameter(ValueFromRemainingArguments)] [string[]]$Arguments
    )

    $cliPath = Get-Command ChoboCli -ErrorAction SilentlyContinue
    if (-not $cliPath) {
        throw 'ChoboCli is not available in the test-runner container yet.'
    }

    & ChoboCli @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ChoboCli failed with exit code $LASTEXITCODE."
    }
}

Export-ModuleMember -Function Invoke-ChoboCli
