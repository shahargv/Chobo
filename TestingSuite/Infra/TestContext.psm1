function ConvertTo-ChoboSafeName {
    param(
        [Parameter(Mandatory)] [string]$Value
    )

    ($Value.ToLowerInvariant() -replace '[^a-z0-9_]', '_').Trim('_')
}

function Get-ChoboShortHash {
    param(
        [Parameter(Mandatory)] [string]$Value,
        [int]$Length = 10
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData($bytes)
    $hash = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
    $hash.Substring(0, $Length)
}

function Limit-ChoboNamePart {
    param(
        [Parameter(Mandatory)] [string]$Value,
        [int]$Length = 20
    )

    $safe = ConvertTo-ChoboSafeName -Value $Value
    if ($safe.Length -le $Length) {
        return $safe
    }

    $safe.Substring(0, $Length).Trim('_')
}

function ConvertTo-ChoboServiceNamePart {
    param(
        [Parameter(Mandatory)] [string]$Value
    )

    ($Value.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
}

function ConvertTo-ChoboClusterSqlName {
    param(
        [Parameter(Mandatory)] [string]$Value
    )

    $safe = ($Value.ToLowerInvariant() -replace '[^a-z0-9]+', '_').Trim('_')
    "chobo_cluster_$safe"
}

$script:ChoboDefaultS3Bucket = 'data-bucket'
$script:ChoboDefaultS3AccessKey = 'chobo-access-key'
$script:ChoboDefaultS3SecretKey = 'chobo-secret-key'

function New-ChoboResourceContext {
    param(
        [Parameter(Mandatory)] [string]$RunId,
        [Parameter(Mandatory)] [string]$TestName,
        [Parameter(Mandatory)] [hashtable]$Definition
    )

    $name = $Definition.Name
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'Every resource definition must include Name.'
    }

    $type = if ($Definition.Type) { $Definition.Type } else { 'SingleNode' }
    $safeResource = Limit-ChoboNamePart -Value $name -Length 18
    $safeServiceResource = ConvertTo-ChoboServiceNamePart -Value $name
    $shortRunId = Limit-ChoboNamePart -Value $RunId -Length 18
    $safeTest = Limit-ChoboNamePart -Value $TestName -Length 16
    $databaseName = "chobo_{0}_{1}_{2}" -f $shortRunId, $safeTest, $safeResource
    $tableName = "tbl_{0}" -f $safeResource

    if ($type -eq 'S3') {
        $host = if ($Definition.Host) { $Definition.Host } else { 'minio' }
        $dnsName = if ($Definition.DnsName) { $Definition.DnsName } else { $host }
        return [pscustomobject]@{
            Name = $name
            Type = 'S3'
            Host = $host
            DnsName = $dnsName
            Port = 9000
            Endpoint = "http://$dnsName`:9000"
            Bucket = $script:ChoboDefaultS3Bucket
            AccessKey = $script:ChoboDefaultS3AccessKey
            SecretKey = $script:ChoboDefaultS3SecretKey
            DatabaseName = $null
            TableName = $null
        }
    }

    if ($type -eq 'ChoboServer') {
        $host = if ($Definition.Host) { $Definition.Host } else { 'choboserver' }
        $dnsName = if ($Definition.DnsName) { $Definition.DnsName } else { $host }
        return [pscustomobject]@{
            Name = $name
            Type = 'ChoboServer'
            Host = $host
            DnsName = $dnsName
            Port = 8080
            DatabaseName = $null
            TableName = $null
        }
    }

    if ($type -eq 'Cluster') {
        $shards = if ($Definition.Shards) { [int]$Definition.Shards } else { 1 }
        $replicas = if ($Definition.Replicas) { [int]$Definition.Replicas } else { 2 }
        if ($shards -lt 1 -or $replicas -lt 1) {
            throw "Cluster resource '$name' must use Shards and Replicas values greater than zero."
        }

        $defaultHost = "clickhouse-$safeServiceResource-s1-r1"
        $defaultReplicaHost = if ($replicas -gt 1) { "clickhouse-$safeServiceResource-s1-r2" } else { $defaultHost }
        $host = if ($Definition.Host) { $Definition.Host } else { $defaultHost }
        $replicaHost = if ($Definition.ReplicaHost) { $Definition.ReplicaHost } else { $defaultReplicaHost }
        $clusterName = if ($Definition.ClusterName) { $Definition.ClusterName } else { ConvertTo-ChoboClusterSqlName -Value $name }
        $dnsName = if ($Definition.DnsName) { $Definition.DnsName } else { "clickhouse-$safeServiceResource" }
        return [pscustomobject]@{
            Name = $name
            Type = 'Cluster'
            Shards = $shards
            Replicas = $replicas
            Host = $host
            DnsName = $dnsName
            Port = 9000
            ReplicaHost = $replicaHost
            ClusterName = $clusterName
            DatabaseName = $databaseName
            TableName = $tableName
        }
    }

    $host = if ($Definition.Host) { $Definition.Host } else { "clickhouse-$safeServiceResource" }
    $dnsName = if ($Definition.DnsName) { $Definition.DnsName } else { $host }
    [pscustomobject]@{
        Name = $name
        Type = 'SingleNode'
        Host = $host
        DnsName = $dnsName
        Port = 9000
        ReplicaHost = $null
        ClusterName = $null
        DatabaseName = $databaseName
        TableName = $tableName
    }
}

function New-ChoboTestContext {
    param(
        [Parameter(Mandatory)] [string]$RunId,
        [Parameter(Mandatory)] [string]$TestName,
        [Parameter(Mandatory)] [string]$OutputDirectory,
        [object[]]$ResourceDefinitions = @()
    )

    $safeTest = Limit-ChoboNamePart -Value $TestName -Length 20
    $testId = $safeTest
    $resources = [ordered]@{}
    foreach ($definition in @($ResourceDefinitions)) {
        $hash = @{}
        if ($definition -is [hashtable]) {
            foreach ($entry in $definition.GetEnumerator()) {
                $hash[$entry.Key] = $entry.Value
            }
        } elseif ($definition -is [System.Collections.IEnumerable] -and -not ($definition -is [string])) {
            foreach ($entry in $definition) {
                if ($entry -is [hashtable]) {
                    foreach ($hashEntry in $entry.GetEnumerator()) {
                        $hash[$hashEntry.Key] = $hashEntry.Value
                    }
                } elseif ($entry.PSObject.Properties.Name -contains 'Key' -and $entry.PSObject.Properties.Name -contains 'Value') {
                    $hash[$entry.Key] = $entry.Value
                }
            }
        } else {
            foreach ($property in $definition.PSObject.Properties) {
                $hash[$property.Name] = $property.Value
            }
        }
        $resource = New-ChoboResourceContext -RunId $RunId -TestName $TestName -Definition $hash
        $resources[$resource.Name] = $resource
    }

    if ($resources.Count -eq 0) {
        $resource = New-ChoboResourceContext -RunId $RunId -TestName $TestName -Definition @{ Name = 'source'; Type = 'SingleNode' }
        $resources[$resource.Name] = $resource
    }

    $defaultResourceName = @($resources.Keys)[0]
    $defaultResource = $resources[$defaultResourceName]

    [pscustomobject]@{
        RunId = $RunId
        TestName = $TestName
        TestId = $testId
        OutputDirectory = $OutputDirectory
        Resources = [pscustomobject]$resources
        DefaultResourceName = $defaultResourceName
        DatabaseName = $defaultResource.DatabaseName
        TableName = $defaultResource.TableName
    }
}

Export-ModuleMember -Function New-ChoboTestContext, ConvertTo-ChoboSafeName
