function Get-ChoboResourceDefinitions {
    param(
        [Parameter(Mandatory)] $TestDefinition
    )

    if ($TestDefinition.Resources) {
        return @($TestDefinition.Resources)
    }

    @(@{ Name = 'source'; Type = 'SingleNode' })
}

Export-ModuleMember -Function Get-ChoboResourceDefinitions
