@{
    Name = 'FailingBasicTest'
    Description = 'Intentionally fails so the suite failure path, reports, and artifacts can be tested.'
    ExcludeFromRunAll = $true
    EnvironmentReuseGroup = 'clickhouse'
    Resources = @(
        @{ Name = 'source'; Type = 'SingleNode'; DnsName = 'failing-source-single' }
    )

    Setup = @(
        @{
            Name = 'create-table'
            Type = 'Sql'
            Path = 'Sql/create-table.sql'
        }
        @{
            Name = 'insert-rows'
            Type = 'Sql'
            Path = 'Sql/insert-rows.sql'
        }
    )

    Action = @()

    Verify = @(
        @{
            Name = 'verify-intentional-mismatch'
            Type = 'Csv'
            Path = 'Sql/select-rows.sql'
            Expected = 'Expected/wrong-rows.csv'
            Actual = 'actual.csv'
        }
    )

    Cleanup = @()
}
