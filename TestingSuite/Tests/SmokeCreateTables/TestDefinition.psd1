@{
    Name = 'SmokeCreateTables'
    Description = 'Creates tables on one standalone node and two clusters, inserts deterministic data, and validates each resource.'
    EnvironmentReuseGroup = 'clickhouse'
    Resources = @(
        @{ Name = 'single'; Type = 'SingleNode'; DnsName = 'smoke-single' }
        @{ Name = 'cluster1'; Type = 'Cluster'; Shards = 1; Replicas = 2; DnsName = 'smoke-cluster-1' }
        @{ Name = 'cluster2'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'smoke-cluster-2' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'smoke-s3' }
    )

    Setup = @(
        @{ Name = 'create-single-table'; Type = 'Sql'; Resource = 'single'; Path = 'Sql/create-single-table.sql' }
        @{ Name = 'insert-single-rows'; Type = 'Sql'; Resource = 'single'; Path = 'Sql/insert-single-rows.sql' }

        @{ Name = 'create-cluster1-table'; Type = 'Sql'; Resource = 'cluster1'; Path = 'Sql/create-cluster1-table.sql' }
        @{ Name = 'insert-cluster1-rows'; Type = 'Sql'; Resource = 'cluster1'; Path = 'Sql/insert-cluster1-rows.sql' }

        @{ Name = 'create-cluster2-table'; Type = 'Sql'; Resource = 'cluster2'; Path = 'Sql/create-cluster2-table.sql' }
        @{ Name = 'insert-cluster2-rows'; Type = 'Sql'; Resource = 'cluster2'; Path = 'Sql/insert-cluster2-rows.sql' }
    )

    Action = @()

    Verify = @(
        @{
            Name = 'verify-single'
            Type = 'Csv'
            Resource = 'single'
            Path = 'Sql/select-single-rows.sql'
            Expected = 'Expected/single.csv'
            Actual = 'single.csv'
        }
        @{
            Name = 'verify-cluster1-primary'
            Type = 'Csv'
            Resource = 'cluster1'
            Path = 'Sql/select-cluster1-rows.sql'
            Expected = 'Expected/cluster1.csv'
            Actual = 'cluster1-primary.csv'
        }
        @{
            Name = 'verify-cluster1-replica'
            Type = 'Csv'
            Resource = 'cluster1'
            Host = '{cluster1.ReplicaHost}'
            Path = 'Sql/select-cluster1-rows.sql'
            Expected = 'Expected/cluster1.csv'
            Actual = 'cluster1-replica.csv'
        }
        @{
            Name = 'verify-cluster2-primary'
            Type = 'Csv'
            Resource = 'cluster2'
            Path = 'Sql/select-cluster2-rows.sql'
            Expected = 'Expected/cluster2.csv'
            Actual = 'cluster2-primary.csv'
        }
        @{
            Name = 'verify-cluster2-replica'
            Type = 'Csv'
            Resource = 'cluster2'
            Host = '{cluster2.ReplicaHost}'
            Path = 'Sql/select-cluster2-rows.sql'
            Expected = 'Expected/cluster2.csv'
            Actual = 'cluster2-replica.csv'
        }
    )

    Cleanup = @()
}
