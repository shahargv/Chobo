function ConvertTo-ChoboComposePath {
    param([Parameter(Mandatory)] [string]$Path)
    [System.IO.Path]::GetFullPath($Path).Replace('\', '/')
}

function ConvertTo-ChoboResourceHashtable {
    param([Parameter(Mandatory)] $Resource)

    $hash = @{}
    if ($Resource -is [hashtable]) {
        foreach ($entry in $Resource.GetEnumerator()) {
            $hash[$entry.Key] = $entry.Value
        }
    } elseif ($Resource -is [System.Collections.IEnumerable] -and -not ($Resource -is [string])) {
        foreach ($entry in $Resource) {
            if ($entry -is [hashtable]) {
                foreach ($hashEntry in $entry.GetEnumerator()) {
                    $hash[$hashEntry.Key] = $hashEntry.Value
                }
            } elseif ($entry.PSObject.Properties.Name -contains 'Key' -and $entry.PSObject.Properties.Name -contains 'Value') {
                $hash[$entry.Key] = $entry.Value
            }
        }
    } else {
        foreach ($property in $Resource.PSObject.Properties) {
            $hash[$property.Name] = $property.Value
        }
    }

    $hash
}

function ConvertTo-ChoboNamePart {
    param([Parameter(Mandatory)] [string]$Value)

    $safe = ($Value.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        throw "Cannot create a service name from '$Value'."
    }

    $safe
}

function ConvertTo-ChoboClusterSqlName {
    param([Parameter(Mandatory)] [string]$Value)

    $safe = ($Value.ToLowerInvariant() -replace '[^a-z0-9]+', '_').Trim('_')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        throw "Cannot create a ClickHouse cluster name from '$Value'."
    }

    "chobo_cluster_$safe"
}

function Get-ChoboSelectedTestsForCompose {
    param(
        [Parameter(Mandatory)] [string]$SuiteRoot,
        [string[]]$TestName = @()
    )

    Import-Module (Join-Path $SuiteRoot 'Infra/TestDiscovery.psm1') -Force

    $tests = Get-ChoboTests -TestsRoot (Join-Path $SuiteRoot 'Tests')
    if ($TestName.Count -gt 0) {
        $nameSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $TestName) {
            [void]$nameSet.Add($name)
        }
        $tests = @($tests | Where-Object { $nameSet.Contains($_.Name) })
    } else {
        $tests = @($tests | Where-Object { -not $_.ExcludeFromRunAll })
    }

    if ($tests.Count -eq 0) {
        throw 'No matching tests found.'
    }

    @($tests)
}

function Get-ChoboRequiredResourcesForCompose {
    param(
        [Parameter(Mandatory)] [string]$SuiteRoot,
        [Parameter(Mandatory)] [object[]]$Tests
    )

    Import-Module (Join-Path $SuiteRoot 'Infra/ResourceRequirements.psm1') -Force

    $resources = New-Object System.Collections.Generic.List[hashtable]
    foreach ($test in $Tests) {
        foreach ($resource in @(Get-ChoboResourceDefinitions -TestDefinition $test)) {
            $resources.Add((ConvertTo-ChoboResourceHashtable -Resource $resource))
        }
    }

    $resources.ToArray()
}

function Get-ChoboSingleServiceName {
    param([Parameter(Mandatory)] [string]$ResourceName)
    "clickhouse-$(ConvertTo-ChoboNamePart -Value $ResourceName)"
}

function Get-ChoboKeeperServiceName {
    param([Parameter(Mandatory)] [string]$ResourceName)
    "clickhouse-keeper-$(ConvertTo-ChoboNamePart -Value $ResourceName)"
}

function Get-ChoboClusterServiceName {
    param(
        [Parameter(Mandatory)] [string]$ResourceName,
        [Parameter(Mandatory)] [int]$Shard,
        [Parameter(Mandatory)] [int]$Replica
    )

    "clickhouse-$(ConvertTo-ChoboNamePart -Value $ResourceName)-s$Shard-r$Replica"
}

function Get-ChoboClusterName {
    param([Parameter(Mandatory)] [hashtable]$Resource)

    if ($Resource.ClusterName) {
        return $Resource.ClusterName
    }

    ConvertTo-ChoboClusterSqlName -Value $Resource.Name
}

function New-ChoboComposePlan {
    param([Parameter(Mandatory)] [hashtable[]]$Resources)

    $singleResources = [ordered]@{}
    $clusters = [ordered]@{}
    $hasStorage = $false
    $hasChoboServer = $false

    foreach ($resource in $Resources) {
        $name = $resource.Name
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw 'Every resource definition must include Name.'
        }

        $type = if ($resource.Type) { $resource.Type } else { 'SingleNode' }

        switch ($type) {
            'SingleNode' {
                if (-not $singleResources.Contains($name)) {
                    $singleResources[$name] = [pscustomobject]@{
                        Name = $name
                        ServiceName = Get-ChoboSingleServiceName -ResourceName $name
                    }
                }
            }
            'Cluster' {
                $shards = if ($resource.Shards) { [int]$resource.Shards } else { 1 }
                $replicas = if ($resource.Replicas) { [int]$resource.Replicas } else { 2 }
                if ($shards -lt 1 -or $replicas -lt 1) {
                    throw "Cluster resource '$name' must use Shards and Replicas values greater than zero."
                }

                $clusterName = Get-ChoboClusterName -Resource $resource
                if ($clusters.Contains($name)) {
                    $existing = $clusters[$name]
                    if ($existing.Shards -ne $shards -or $existing.Replicas -ne $replicas -or $existing.ClusterName -ne $clusterName) {
                        throw "Cluster resource '$name' is requested with conflicting layouts."
                    }
                } else {
                    $clusters[$name] = [pscustomobject]@{
                        Name = $name
                        SafeName = ConvertTo-ChoboNamePart -Value $name
                        ClusterName = $clusterName
                        KeeperServiceName = Get-ChoboKeeperServiceName -ResourceName $name
                        Shards = $shards
                        Replicas = $replicas
                    }
                }
            }
            'S3' {
                $hasStorage = $true
            }
            'ChoboServer' {
                $hasChoboServer = $true
            }
            default {
                throw "Unsupported resource type '$type'."
            }
        }
    }

    [pscustomobject]@{
        SingleResources = @($singleResources.Values)
        Clusters = @($clusters.Values)
        HasStorage = $hasStorage
        HasChoboServer = $hasChoboServer
    }
}

function Write-ChoboKeeperConfig {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$KeeperServiceName
    )

    @"
<clickhouse>
  <logger>
    <level>information</level>
    <console>true</console>
  </logger>
  <listen_host>0.0.0.0</listen_host>
  <keeper_server>
    <tcp_port>9181</tcp_port>
    <server_id>1</server_id>
    <log_storage_path>/var/lib/clickhouse/coordination/log</log_storage_path>
    <snapshot_storage_path>/var/lib/clickhouse/coordination/snapshots</snapshot_storage_path>
    <coordination_settings>
      <operation_timeout_ms>10000</operation_timeout_ms>
    </coordination_settings>
    <raft_configuration>
      <server>
        <id>1</id>
        <hostname>$KeeperServiceName</hostname>
        <port>9234</port>
      </server>
    </raft_configuration>
  </keeper_server>
</clickhouse>
"@ | Set-Content -Path $Path
}

function Write-ChoboClusterConfig {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] $Cluster
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('<clickhouse>')
    $lines.Add('  <listen_host>0.0.0.0</listen_host>')
    $lines.Add('  <remote_servers>')
    $lines.Add("    <$($Cluster.ClusterName)>")
    for ($shard = 1; $shard -le $Cluster.Shards; $shard++) {
        $lines.Add('      <shard>')
        $lines.Add('        <internal_replication>true</internal_replication>')
        for ($replica = 1; $replica -le $Cluster.Replicas; $replica++) {
            $serviceName = Get-ChoboClusterServiceName -ResourceName $Cluster.Name -Shard $shard -Replica $replica
            $lines.Add('        <replica>')
            $lines.Add("          <host>$serviceName</host>")
            $lines.Add('          <port>9000</port>')
            $lines.Add('        </replica>')
        }
        $lines.Add('      </shard>')
    }
    $lines.Add("    </$($Cluster.ClusterName)>")
    $lines.Add('  </remote_servers>')
    $lines.Add('  <zookeeper>')
    $lines.Add('    <node>')
    $lines.Add("      <host>$($Cluster.KeeperServiceName)</host>")
    $lines.Add('      <port>9181</port>')
    $lines.Add('    </node>')
    $lines.Add('  </zookeeper>')
    $lines.Add('</clickhouse>')

    $lines -join [Environment]::NewLine | Set-Content -Path $Path
}

function Write-ChoboMacrosConfig {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ClusterName,
        [Parameter(Mandatory)] [int]$Shard,
        [Parameter(Mandatory)] [int]$Replica
    )

    @"
<clickhouse>
  <macros>
    <cluster>$ClusterName</cluster>
    <shard>shard$Shard</shard>
    <replica>replica$Replica</replica>
  </macros>
</clickhouse>
"@ | Set-Content -Path $Path
}

function Add-ChoboClickHouseServiceYaml {
    param(
        [Parameter(Mandatory)] $Lines,
        [Parameter(Mandatory)] [string]$ServiceName,
        [string[]]$Volumes = @(),
        [string[]]$DependsOn = @(),
        [string[]]$Aliases = @()
    )

    $Lines.Add("  ${ServiceName}:")
    $Lines.Add('    labels:')
    $Lines.Add('      chobo.system-test: "true"')
    $Lines.Add('    image: clickhouse/clickhouse-server:25.4')
    $Lines.Add('    environment:')
    $Lines.Add('      CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: "1"')
    if ($Volumes.Count -gt 0) {
        $Lines.Add('    volumes:')
        foreach ($volume in $Volumes) {
            $Lines.Add("      - $volume")
        }
    }
    if ($DependsOn.Count -gt 0) {
        $Lines.Add('    depends_on:')
        foreach ($dependency in $DependsOn) {
            $Lines.Add("      - $dependency")
        }
    }
    $Lines.Add('    ulimits:')
    $Lines.Add('      nofile:')
    $Lines.Add('        soft: 262144')
    $Lines.Add('        hard: 262144')
    if ($Aliases.Count -gt 0) {
        $Lines.Add('    networks:')
        $Lines.Add('      chobo-tests:')
        $Lines.Add('        aliases:')
        foreach ($alias in $Aliases) {
            $Lines.Add("          - $alias")
        }
    } else {
        $Lines.Add('    networks:')
        $Lines.Add('      - chobo-tests')
    }
    $Lines.Add('')
}

function New-ChoboComposeEnvironment {
    param(
        [Parameter(Mandatory)] [string]$SuiteRoot,
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$OutputDirectory,
        [string[]]$TestName = @()
    )

    $tests = Get-ChoboSelectedTestsForCompose -SuiteRoot $SuiteRoot -TestName $TestName
    $resources = Get-ChoboRequiredResourcesForCompose -SuiteRoot $SuiteRoot -Tests $tests
    $plan = New-ChoboComposePlan -Resources $resources

    $composeRoot = Join-Path $OutputDirectory 'generated-compose'
    $configRoot = Join-Path $composeRoot 'config'
    New-Item -ItemType Directory -Force -Path $composeRoot, $configRoot | Out-Null

    $suitePath = ConvertTo-ChoboComposePath -Path $SuiteRoot
    $repoPath = ConvertTo-ChoboComposePath -Path $RepoRoot
    $outputPath = ConvertTo-ChoboComposePath -Path $OutputDirectory
    $runnerDockerfile = ConvertTo-ChoboComposePath -Path (Join-Path $SuiteRoot 'Compose/test-runner/Dockerfile')

    $lines = New-Object System.Collections.Generic.List[string]
    $services = New-Object System.Collections.Generic.List[string]

    $lines.Add('services:')
    $lines.Add('  test-runner:')
    $lines.Add('    labels:')
    $lines.Add('      chobo.system-test: "true"')
    $lines.Add('    build:')
    $lines.Add("      context: `"$repoPath`"")
    $lines.Add("      dockerfile: `"$runnerDockerfile`"")
    $lines.Add('    command: ["sleep", "infinity"]')
    $lines.Add('    volumes:')
    $lines.Add("      - `"${suitePath}:/suite:ro`"")
    $lines.Add("      - `"${outputPath}:/results`"")
    $lines.Add('    networks:')
    $lines.Add('      - chobo-tests')
    $lines.Add('')
    $services.Add('test-runner')

    if ($plan.HasStorage) {
        $storageAliases = @($resources | Where-Object { $_.Type -eq 'S3' -and $_.DnsName } | ForEach-Object { $_.DnsName } | Select-Object -Unique)
        $lines.Add('  minio:')
        $lines.Add('    labels:')
        $lines.Add('      chobo.system-test: "true"')
        $lines.Add('    image: minio/minio:RELEASE.2025-09-07T16-13-09Z')
        $lines.Add('    command: ["server", "/data", "--console-address", ":9001"]')
        $lines.Add('    environment:')
        $lines.Add('      MINIO_ROOT_USER: chobo-access-key')
        $lines.Add('      MINIO_ROOT_PASSWORD: chobo-secret-key')
        $lines.Add('    networks:')
        if ($storageAliases.Count -gt 0) {
            $lines.Add('      chobo-tests:')
            $lines.Add('        aliases:')
            foreach ($alias in $storageAliases) {
                $lines.Add("          - $alias")
            }
        } else {
            $lines.Add('      - chobo-tests')
        }
        $lines.Add('')
        $services.Add('minio')

        $lines.Add('  minio-init:')
        $lines.Add('    labels:')
        $lines.Add('      chobo.system-test: "true"')
        $lines.Add('    image: minio/mc:RELEASE.2025-08-13T08-35-41Z')
        $lines.Add('    depends_on:')
        $lines.Add('      - minio')
        $lines.Add('    entrypoint:')
        $lines.Add('      - /bin/sh')
        $lines.Add('      - -c')
        $lines.Add('      - |')
        $lines.Add('        until mc alias set chobo http://minio:9000 chobo-access-key chobo-secret-key; do sleep 1; done')
        $lines.Add('        mc mb --ignore-existing chobo/data-bucket')
        $lines.Add('    networks:')
        $lines.Add('      - chobo-tests')
        $lines.Add('')
        $services.Add('minio-init')
    }

    foreach ($single in $plan.SingleResources) {
        Add-ChoboClickHouseServiceYaml -Lines $lines -ServiceName $single.ServiceName
        $services.Add($single.ServiceName)
    }

    foreach ($cluster in $plan.Clusters) {
        $clusterConfigPath = Join-Path $configRoot "$($cluster.SafeName)-cluster.xml"
        $keeperConfigPath = Join-Path $configRoot "$($cluster.SafeName)-keeper.xml"
        Write-ChoboClusterConfig -Path $clusterConfigPath -Cluster $cluster
        Write-ChoboKeeperConfig -Path $keeperConfigPath -KeeperServiceName $cluster.KeeperServiceName

        $keeperConfigVolume = "$(ConvertTo-ChoboComposePath -Path $keeperConfigPath):/etc/clickhouse-keeper/keeper_config.xml:ro"
        $lines.Add("  $($cluster.KeeperServiceName):")
        $lines.Add('    labels:')
        $lines.Add('      chobo.system-test: "true"')
        $lines.Add('    image: clickhouse/clickhouse-server:25.4')
        $lines.Add('    command: ["clickhouse-keeper", "--config-file=/etc/clickhouse-keeper/keeper_config.xml"]')
        $lines.Add('    volumes:')
        $lines.Add("      - `"$keeperConfigVolume`"")
        $lines.Add('    networks:')
        $lines.Add('      - chobo-tests')
        $lines.Add('')
        $services.Add($cluster.KeeperServiceName)

        $clusterConfigVolume = "$(ConvertTo-ChoboComposePath -Path $clusterConfigPath):/etc/clickhouse-server/config.d/cluster.xml:ro"
        for ($shard = 1; $shard -le $cluster.Shards; $shard++) {
            for ($replica = 1; $replica -le $cluster.Replicas; $replica++) {
                $serviceName = Get-ChoboClusterServiceName -ResourceName $cluster.Name -Shard $shard -Replica $replica
                $macrosPath = Join-Path $configRoot "$serviceName-macros.xml"
                Write-ChoboMacrosConfig -Path $macrosPath -ClusterName $cluster.ClusterName -Shard $shard -Replica $replica
                $macrosVolume = "$(ConvertTo-ChoboComposePath -Path $macrosPath):/etc/clickhouse-server/config.d/macros.xml:ro"
                Add-ChoboClickHouseServiceYaml -Lines $lines -ServiceName $serviceName -Volumes @("`"$clusterConfigVolume`"", "`"$macrosVolume`"") -DependsOn @($cluster.KeeperServiceName)
                $services.Add($serviceName)
            }
        }
    }

    if ($plan.HasChoboServer) {
        $lines.Add('  choboserver:')
        $lines.Add('    labels:')
        $lines.Add('      chobo.system-test: "true"')
        $lines.Add('    build:')
        $lines.Add("      context: `"$repoPath`"")
        $lines.Add('      dockerfile: ChoboServer/Dockerfile')
        $lines.Add('    environment:')
        $lines.Add('      CHOBO_INIT_ADMIN_USER: admin')
        $lines.Add('      CHOBO_INIT_ACCESS_TOKEN: static-test-token')
        $lines.Add('      Chobo__DataDirectory: /tmp/chobo-data')
        $lines.Add('      Chobo__BackupRestore__SchedulerInterval: "00:00:01"')
        $lines.Add('      Chobo__BackupRestore__PollInterval: "00:00:01"')
        $lines.Add('    networks:')
        $lines.Add('      - chobo-tests')
        $lines.Add('')
        $services.Add('choboserver')
    }

    $lines.Add('networks:')
    $lines.Add('  chobo-tests:')
    $lines.Add('    driver: bridge')

    $composeFile = Join-Path $composeRoot 'docker-compose.generated.yml'
    $lines -join [Environment]::NewLine | Set-Content -Path $composeFile

    [pscustomobject]@{
        ComposeFile = $composeFile
        ComposeRoot = $composeRoot
        Services = @($services.ToArray())
        HasStorage = $plan.HasStorage
        Tests = $tests
        Resources = $resources
        Plan = $plan
    }
}

Export-ModuleMember -Function New-ChoboComposeEnvironment, Get-ChoboClusterServiceName, Get-ChoboSingleServiceName, Get-ChoboKeeperServiceName
