---
name: chobo-system-tests
description: Use when working on Chobo system tests in TestingSuite, including creating tests, running Docker Compose tests, adding named resources, inspecting JSON/HTML results, or debugging ClickHouse/MinIO/test-runner logs.
---

# Chobo System Tests

Use `TestingSuite/README.md` as the source of truth. Keep tests declarative when possible, and run them through `TestingSuite/TestManager.ps1`; test logic executes inside the generated `test-runner` container.

## Execute Tests

Always pass `-TestId` so artifacts are easy to find:

```powershell
.\TestingSuite\TestManager.ps1 -ListTests
.\TestingSuite\TestManager.ps1 -TestId codex-smoke-<yyyyMMdd-HHmmss> -TestName SmokeCreateTables
.\TestingSuite\TestManager.ps1 -TestId codex-failure-<yyyyMMdd-HHmmss> -TestName FailingBasicTest
.\TestingSuite\TestManager.ps1 -TestId codex-debug-<yyyyMMdd-HHmmss> -TestName SmokeCreateTables -KeepEnvironment
.\TestingSuite\TestManager.ps1 -TestId codex-final-<yyyyMMdd-HHmmss> -TestName SmokeCreateTables -CleanTestResults
```

Results default to `.artifacts/TestResults/<test-id>`. Inspect:

- `results.json`
- `index.html`
- `generated-compose/docker-compose.generated.yml`
- `generated-compose/config/*`
- `logs/*`
- `artifacts/<TestName>/*`

Use `-GlobalTimeoutSeconds` for the whole run and `-TestTimeoutSeconds` for each test. After a mission succeeds, run the final verification with `-CleanTestResults` so only the final result folder remains. Do not clean before debugging a failure.

Avoid running multiple `TestManager.ps1` invocations at the same time. Docker-heavy Chobo system tests should be run sequentially so local CPU/memory pressure does not disturb results.

## Create Tests

Prefer declarative tests:

- Add `TestingSuite/Tests/<TestName>/TestDefinition.psd1`.
- Add setup/action/verify SQL under `TestingSuite/Tests/<TestName>/Sql`.
- Add expected CSVs under `TestingSuite/Tests/<TestName>/Expected`.
- Use `SmokeCreateTables` as the reference shape.

Resource `Name` is the identity. Do not add `Instance`. Declare only the resources the test needs:

```powershell
Resources = @(
    @{ Name = 'source'; Type = 'Cluster'; Shards = 2; Replicas = 2; DnsName = 'source-cluster' }
    @{ Name = 'restore'; Type = 'SingleNode'; DnsName = 'restore-node' }
    @{ Name = 'backupStore'; Type = 'S3'; DnsName = 'backup-s3' }
)
```

Supported resources:

- `SingleNode`: generated service `clickhouse-<resource-name>`.
- `Cluster`: generated services `clickhouse-<resource-name>-s<shard>-r<replica>` and a dedicated `clickhouse-keeper-<resource-name>`.
- `S3`: MinIO with deterministic bucket and credentials.

S3 constants:

- Bucket: `data-bucket`
- Access key: `chobo-access-key`
- Secret key: `chobo-secret-key`
- Endpoint: `http://{backupStore.DnsName}:9000`

Useful tokens:

- `{RunId}`, `{TestName}`, `{TestId}`
- `{source.Host}`, `{source.DnsName}`
- `{source.ClusterName}`, `{source.ReplicaHost}`, `{source.Shards}`, `{source.Replicas}`
- `{backupStore.Endpoint}`, `{backupStore.Bucket}`, `{backupStore.AccessKey}`, `{backupStore.SecretKey}`

Declarative step types:

- `Sql`: run a SQL file or inline query.
- `Csv`: run a query, write actual CSV, compare with expected CSV.
- `Cli`: run `ChoboCli`, validate exit/text/JSON output, retry until expectations pass, and save JSON values for later steps.

Declarative CLI steps should use `Args` for exact argv entries. Use `Command`/`Query` only for simple command lines. Supported assertion fields:

- `ExpectExitCode`, default `0`.
- `ExpectTextContains` and `ExpectTextNotContains`.
- `ExpectJson` with `Equals`, `NotEmpty`, `Count`, `Contains`, and `ContainsObject`.
- `SaveJsonAs`, which exposes tokens such as `{policy.id}` from saved JSON.
- `RetryTimeoutSeconds` and `RetryIntervalSeconds`.

Example:

```powershell
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

Normal test SQL should create its own databases explicitly, using test-owned database and table names rather than resource-derived database/table placeholders. The infra syncs replicated cluster tables after setup when a table exists and drops databases after successful tests. Set `UseDefaultDatabaseSetup = $true`, `UseDefaultReplicaSync = $false`, or `UseDefaultCleanup = $false` only when a test intentionally wants to override those defaults.

Keep SQL readable. Prefer one explicit SQL file per resource/table shape over conditional placeholder-heavy SQL.

Use custom `Test.ps1` only when declarative steps cannot express the test. In custom tests, use shared helpers from `TestingSuite/Infra`, especially `Invoke-ChoboClickHouseQuery` and `Assert-ChoboCsvEquals`.
