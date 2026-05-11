function Get-ChoboResourceDefinitions {
    param(
        [Parameter(Mandatory)] $TestDefinition
    )

    if ($TestDefinition -is [hashtable] -and $TestDefinition.ContainsKey('Resources')) {
        foreach ($resource in @($TestDefinition['Resources'])) {
            Write-Output -NoEnumerate $resource
        }
        return
    }

    if ($TestDefinition.Resources) {
        foreach ($resource in @($TestDefinition.Resources)) {
            Write-Output -NoEnumerate $resource
        }
        return
    }

    Write-Output -NoEnumerate @{ Name = 'source'; Type = 'SingleNode' }
}

Export-ModuleMember -Function Get-ChoboResourceDefinitions
