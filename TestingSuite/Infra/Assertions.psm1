function Normalize-ChoboText {
    param(
        [string]$Value
    )

    if ($null -eq $Value) {
        return ''
    }

    $normalized = $Value -replace "`r`n", "`n"
    $normalized = $normalized -replace "`r", "`n"
    $normalized.TrimEnd()
}

function Assert-ChoboCsvEquals {
    param(
        [Parameter(Mandatory)] [string]$ExpectedPath,
        [Parameter(Mandatory)] [string]$ActualPath
    )

    $expected = Normalize-ChoboText -Value (Get-Content -Path $ExpectedPath -Raw)
    $actual = Normalize-ChoboText -Value (Get-Content -Path $ActualPath -Raw)

    if ($expected -ne $actual) {
        $diff = Compare-Object -ReferenceObject ($expected -split "`n") -DifferenceObject ($actual -split "`n") | Out-String
        throw "CSV mismatch. Expected: $ExpectedPath Actual: $ActualPath`n$diff"
    }
}

Export-ModuleMember -Function Assert-ChoboCsvEquals
