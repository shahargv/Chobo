function Get-ChoboTests {
    param(
        [Parameter(Mandatory)] [string]$TestsRoot
    )

    $definitions = New-Object System.Collections.Generic.List[object]
    Import-Module (Join-Path $PSScriptRoot 'DeclarativeTests.psm1') -Force

    $declarativeFiles = Get-ChildItem -Path $TestsRoot -Recurse -Filter 'TestDefinition.psd1' | Sort-Object FullName
    foreach ($definitionFile in $declarativeFiles) {
        $definitions.Add((New-ChoboDeclarativeTestDefinition -DefinitionPath $definitionFile.FullName))
    }

    $testFiles = Get-ChildItem -Path $TestsRoot -Recurse -Filter 'Test.ps1' | Sort-Object FullName

    foreach ($testFile in $testFiles) {
        $definitionFile = Join-Path $testFile.DirectoryName 'TestDefinition.psd1'
        if (Test-Path $definitionFile) {
            continue
        }

        . $testFile.FullName
        $definition = Get-ChoboTestDefinition
        $definition | Add-Member -NotePropertyName Path -NotePropertyValue $testFile.FullName -Force
        $definition | Add-Member -NotePropertyName Kind -NotePropertyValue 'PowerShell' -Force
        if (-not ($definition.PSObject.Properties.Name -contains 'ExcludeFromRunAll')) {
            $definition | Add-Member -NotePropertyName ExcludeFromRunAll -NotePropertyValue $false -Force
        }
        if (-not ($definition.PSObject.Properties.Name -contains 'Resources')) {
            $definition | Add-Member -NotePropertyName Resources -NotePropertyValue @(@{ Name = 'source'; Type = 'SingleNode' }) -Force
        }
        $definitions.Add($definition)
    }

    $definitions.ToArray()
}

Export-ModuleMember -Function Get-ChoboTests
