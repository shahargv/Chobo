# Chobo System Test Suite

`TestingSuite` is the Docker Compose based system-test harness for Chobo. For each run, `TestManager.ps1` reads the selected test definitions, generates the requested Docker Compose environment, starts it, runs the tests inside a `test-runner` container, collects artifacts/logs, and tears everything down.

The suite is resource-first. A test declares named resources such as `source`, `restore`, or `backupStore`; setup/action/verification steps target those names. There is no scenario matrix and no static ClickHouse topology.

## Running Tests

Run from the repository root:

```powershell
.\TestingSuite\TestManager.ps1 -TestId dev-smoke-001
```

Common commands:

```powershell
.\TestingSuite\TestManager.ps1 -ListTests
.\TestingSuite\TestManager.ps1 -TestId dev-smoke-001 -TestName SmokeCreateTables
.\TestingSuite\TestManager.ps1 -TestId dev-failure-001 -TestName FailingBasicTest
.\TestingSuite\TestManager.ps1 -TestId dev-smoke-001 -OutputDirectory C:\tmp\chobo-results
.\TestingSuite\TestManager.ps1 -TestId dev-smoke-001 -GlobalTimeoutSeconds 1800 -TestTimeoutSeconds 300
.\TestingSuite\TestManager.ps1 -TestId dev-runall-001 -RunAllConcurrency 3
.\TestingSuite\TestManager.ps1 -TestId dev-smoke-001 -KeepEnvironment
.\TestingSuite\TestManager.ps1 -TestId dev-final-001 -TestName SmokeCreateTables -CleanTestResults
```

`TestId` is the run identifier. If omitted, one is generated from timestamp and GUID. Results default to `.artifacts/TestResults/<test-id>`. If `-OutputDirectory` is supplied, results go to `<output-directory>/<test-id>`.

`GlobalTimeoutSeconds` caps Compose startup plus runner execution. `TestTimeoutSeconds` is the default timeout per test. A test can override it with `TimeoutSeconds`.

When running the full suite without `-TestName`, `TestManager.ps1` runs up to three tests concurrently by default. Override that with `-RunAllConcurrency <n>` when a machine needs more or less Docker pressure.

`CleanTestResults` clears the configured result root before creating the current run directory. Use it for the final successful verification run to avoid accumulating old artifacts. Avoid it while debugging a failure you still need to inspect.

`KeepEnvironment` leaves the generated Compose project running for manual debugging. Normal runs tear down containers and remove leftover `chobo.system-test=true` containers.

## Output

Each run writes:

- `results.json`: machine-readable run/test status, timings, timeout flags, errors, and artifact paths.
- `index.html`: human-readable report.
- `generated-compose/docker-compose.generated.yml`: the exact Docker Compose file used by the run.
- `generated-compose/config/*`: generated ClickHouse Keeper, cluster, and macro config.
- `logs/*`: Docker Compose command output and per-service logs.
- `artifacts/<TestName>/*`: expanded SQL, actual CSVs, command output, and failure details.

## Folder Layout

- `TestManager.ps1`: host entry point and environment orchestration.
- `Runner/Run-Tests.ps1`: executes inside `test-runner`.
- `Infra`: shared PowerShell modules for Compose generation, resource context, ClickHouse, assertions, discovery, declarative tests, and reporting.
- `Compose/test-runner`: base Dockerfile for the generated `test-runner` service.
- `Tests/<TestName>`: test definitions, SQL files, expected CSVs, and test-owned assets.

## Resources

Declare resources in `TestDefinition.psd1`. Resource `Name` is the identity; do not add `Instance`.

```powershell
Resources = @(
    @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'source-cluster' }
    @{ Name = 'restore'; Type = 'SingleNode'; DnsName = 'restore-node' }
    @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
)
```

Supported types:

- `SingleNode`: standalone ClickHouse. Service name: `clickhouse-<resource-name>`, for example `clickhouse-restore`.
- `Cluster`: ClickHouse cluster. Requires or defaults `Shards = 1` and `Replicas = 2`. Services: `clickhouse-<resource-name>-s<shard>-r<replica>`, for example `clickhouse-source-s2-r1`. Each cluster gets its own generated Keeper: `clickhouse-keeper-<resource-name>`.
- `S3`: MinIO. The bucket and credentials are deterministic constants.

S3 constants:

```text
Bucket:     data-bucket
Access key: chobo-access-key
Secret key: chobo-secret-key
Endpoint:   http://<DnsName>:9000
```

The generated Compose file includes only resources required by the selected tests. A single-node test emits only the runner and that ClickHouse node. A test with two clusters emits two independent cluster layouts and two independent Keepers.

`DnsName` is an alias registered inside the `test-runner` container so future CLI commands can use stable names like `source-cluster` or `backup-s3`.

For cluster resources, `{source.Host}` resolves to shard 1 replica 1. `{source.ReplicaHost}` resolves to shard 1 replica 2 when at least two replicas exist.

When registering a generated cluster with Chobo, pass the generated ClickHouse cluster name to the CLI:

```powershell
Args = @(
    'clusters', 'add',
    '--name', 'source',
    '--mode', 'Cluster',
    '--node', 'clickhouse-source-s1-r1:9000',
    '--clickhouse-cluster-name', '{source.ClusterName}'
)
```

For backup/restore tests, prefer a concrete service name such as `clickhouse-source-s1-r1:9000` over a `DnsName` alias for Chobo's access node. ChoboServer runs in its own container and must be able to resolve the node name; `DnsName` aliases are primarily registered for the `test-runner` container.

## Declarative Tests

Most tests should be declarative: one `TestDefinition.psd1`, SQL files under `Sql`, and expected CSV files under `Expected`.

Minimal example:

```powershell
@{
    Name = 'MyBackupTest'
    Description = 'Creates data, runs a backup command, and validates output.'
    Resources = @(
        @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'source-cluster' }
        @{ Name = 'restore'; Type = 'SingleNode'; DnsName = 'restore-node' }
        @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
    )

    Setup = @(
        @{ Name = 'create-source-table'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/create-source-table.sql' }
        @{ Name = 'insert-source-rows'; Type = 'Sql'; Resource = 'source'; Path = 'Sql/insert-source-rows.sql' }
    )

    Action = @(
        @{
            Name = 'add-policy'
            Type = 'Cli'
            Args = @('policies', 'add', '--name', 'all')
            SaveJsonAs = 'policy'
            ExpectJson = @(
                @{ Path = 'name'; Equals = 'all' }
                @{ Path = 'id'; NotEmpty = $true }
            )
        }
        @{
            Name = 'add-schedule'
            Type = 'Cli'
            Args = @('schedules', 'add', '--name', 'nightly', '--policy-id', '{policy.id}', '--backup-type', 'Full', '--cron', '0 0 2 * * ?', '--timezone', 'UTC')
            ExpectJson = @(
                @{ Path = 'policyId'; Equals = '{policy.id}' }
                @{ Path = 'isEnabled'; Equals = $true }
            )
        }
    )

    Verify = @(
        @{
            Name = 'verify-source'
            Type = 'Csv'
            Resource = 'source'
            Path = 'Sql/select-source-rows.sql'
            Expected = 'Expected/source.csv'
            Actual = 'source.csv'
        }
    )

    Cleanup = @()
}
```

Step types:

- `Sql`: runs a SQL file or inline `Query`.
- `Csv`: runs a query, writes actual CSV, and compares it to `Expected`.
- `Cli`: runs `ChoboCli` from the `test-runner` container and can validate exit code, text output, JSON output, retries, and saved JSON values.

### Declarative CLI Steps

Prefer declarative `Cli` steps for ChoboServer/ChoboCli smoke coverage. Use `Args` for exact argv entries, especially when values contain spaces, and use `Command` or `Query` only for simple command lines.

```powershell
@{
    Name = 'wait-server-api'
    Type = 'Cli'
    Args = @('users', 'list', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
    RetryTimeoutSeconds = 90
    RetryIntervalSeconds = 2
    ExpectJson = @(
        @{ Path = '$'; ContainsObject = @{ userName = 'admin'; isActive = $true } }
    )
}
@{
    Name = 'auth-profile'
    Type = 'Cli'
    Args = @('server', 'auth', '--server-url', 'http://choboserver:8080', '--access-token', 'static-test-token')
    ExpectTextContains = 'Authenticated'
}
@{
    Name = 'add-policy'
    Type = 'Cli'
    Args = @('policies', 'add', '--name', 'all')
    SaveJsonAs = 'policy'
    ExpectJson = @(
        @{ Path = 'name'; Equals = 'all' }
        @{ Path = 'id'; NotEmpty = $true }
    )
}
@{
    Name = 'evaluate-policy'
    Type = 'Cli'
    Args = @('policies', 'evaluate', '--id', '{policy.id}')
    ExpectJson = @(
        @{ Path = 'policyId'; Equals = '{policy.id}' }
    )
}
```

CLI step fields:

- `Args`: array of command arguments passed to `ChoboCli`.
- `Command` or `Query`: string command line; tokens are parsed with PowerShell tokenization.
- `ExpectExitCode`: expected process exit code, default `0`.
- `ExpectTextContains`: string or array of strings that must appear in stdout/stderr.
- `ExpectTextNotContains`: string or array of strings that must not appear in stdout/stderr, useful for credential non-disclosure checks.
- `ExpectJson`: JSON assertions. The command output must be valid JSON.
- `SaveJsonAs`: stores JSON output as a variable. Top-level values become tokens such as `{policy.id}` for later steps.
- `RetryTimeoutSeconds` and `RetryIntervalSeconds`: retry the command and assertions until they pass or timeout.

JSON expectations support:

- `@{ Path = 'name'; Equals = 'all' }`
- `@{ Path = 'id'; NotEmpty = $true }`
- `@{ Path = 'accessNodes'; Count = 1 }`
- `@{ Path = '$'; ContainsObject = @{ name = 'source'; mode = 'Cluster' } }`
- `@{ Path = '$'; Contains = 'some-scalar-value' }`

Each `Sql` or `Csv` step should usually specify `Resource`. Use `Host` only when targeting a specific node, such as `{source.ReplicaHost}`.

Useful tokens:

- `{RunId}`, `{TestName}`, `{TestId}`
- `{source.Host}`, `{source.DnsName}`
- `{source.ClusterName}`, `{source.ReplicaHost}`, `{source.Shards}`, `{source.Replicas}`
- `{backupStore.Endpoint}`, `{backupStore.Bucket}`, `{backupStore.AccessKey}`, `{backupStore.SecretKey}`

The test SQL owns database creation. Put `CREATE DATABASE` statements in setup SQL so each test can choose `Atomic`, replicated, or other database settings explicitly.
Use explicit database and table names in SQL and CLI arguments instead of resource-derived database/table placeholders.

The infra still handles common sync and cleanup:

- Syncs replicated cluster tables after setup when a table exists.
- Drops ClickHouse databases after successful tests.

Only override these defaults when a test intentionally owns that behavior:

```powershell
@{
    UseDefaultDatabaseSetup = $true
    UseDefaultReplicaSync = $false
    UseDefaultCleanup = $false
}
```

Keep SQL readable. Prefer separate explicit SQL files for different resources/table shapes instead of placeholder-heavy SQL.

## Sharded Backup/Restore Tests

`BackupRestoreSharded` is the reference suite for clustered backup/restore behavior. It covers:

- backing up a 2-shard MergeTree table with one selected replica per shard;
- restore with preserved layout into another 2-shard cluster;
- restore of one source shard into a single-node target;
- explicit failure when preserve layout is requested for mismatched shard counts;
- redistribute restore from 2 source shards into a 3-shard target;
- append restore of a selected shard into an existing table;
- backup from a single node and restore into a sharded target;
- operational partial restore where one shard appends successfully and another fails because its target table is incompatible.

The partial restore case is especially important. The expected Chobo behavior is:

- restore run status: `PartiallySucceeded`;
- restore table status: `PartiallySucceeded`;
- successful shard status: `Succeeded`;
- failed shard status: `Failed` with the ClickHouse error in the shard row;
- audit contains `shard-failed`, `table-partially-succeeded`, and restore-level `partially-succeeded`.

Use `backups show`, `restores show`, `audit show`, and per-node `Csv` assertions to verify both metadata and actual data placement. When checking a specific shard, set `Host` to the generated service name, for example:

```powershell
@{ Name = 'verify-shard-two'; Type = 'Csv'; Resource = 'restore'; Host = 'clickhouse-restore-s2-r1'; Path = 'Sql/select-shard-two.sql'; Expected = 'Expected/shard-two.csv' }
```

## Adding A New Test

1. Create `TestingSuite/Tests/<TestName>/TestDefinition.psd1`.
2. Add `Sql/*.sql` files for setup and verification queries.
3. Add `Expected/*.csv` files for `Csv` assertions.
4. Run it with `.\TestingSuite\TestManager.ps1 -TestId dev-<name>-001 -TestName <TestName>`.
5. Inspect `.artifacts/TestResults/<test-id>/results.json`, `index.html`, `artifacts/<TestName>`, and `logs`.
6. After it passes, run one final verification with `-CleanTestResults`.

Use `ExcludeFromRunAll = $true` for intentionally failing/debug-only tests. `FailingBasicTest` is the reference for verifying failure reporting.

## Custom PowerShell Tests

Use custom PowerShell only when the declarative runner cannot express the test clearly. Create `TestingSuite/Tests/<TestName>/Test.ps1` and return a definition from `Get-ChoboTestDefinition`. Prefer shared helpers from `TestingSuite/Infra`, especially `Invoke-ChoboClickHouseQuery` and `Assert-ChoboCsvEquals`.

## Smoke Test

`SmokeCreateTables` validates the harness itself. It declares one standalone node, two clusters with different layouts, and one S3 resource. It creates different table shapes, inserts deterministic rows, validates primary and replica query output, and performs no backup or restore.
