# System Test Suite

Chobo's system test suite checks the product the way an operator uses it: with real ClickHouse nodes, S3-compatible storage, ChoboServer, and ChoboCli running together in Docker Compose.

Use it when a change affects backup or restore behavior, CLI workflows, server integration, audit output, failure handling, or anything that needs more confidence than a unit test can provide.

## What It Does

For each run, the suite creates only the services the selected tests need. A test can ask for a standalone ClickHouse node, one or more ClickHouse clusters, and a MinIO backup store. The test runner then performs setup, runs Chobo commands, verifies ClickHouse data, collects logs and artifacts, and tears the environment down.

Most tests are declarative. A test definition names the resources it needs and lists setup, action, verification, and cleanup steps. SQL files hold ClickHouse setup and verification queries. CSV files hold expected query results. CLI steps exercise Chobo through the same command-line surface a user would use.

## Running Tests

Run tests from the repository root with `TestingSuite/TestManager.ps1`.

```powershell
.\TestingSuite\TestManager.ps1 -ListTests
.\TestingSuite\TestManager.ps1 -TestId dev-smoke-001 -TestName SmokeCreateTables
.\TestingSuite\TestManager.ps1 -TestId dev-final-001 -TestName SmokeCreateTables -CleanTestResults
```

Always choose a `TestId` that makes the run easy to find. Results are written under `.artifacts/TestResults/<test-id>` by default.

Useful run options:

- `-TestName`: runs one named test instead of the default set.
- `-GlobalTimeoutSeconds`: caps the full Compose and test run.
- `-TestTimeoutSeconds`: caps each selected test.
- `-KeepEnvironment`: leaves containers running so you can inspect them manually.
- `-CleanTestResults`: clears old result folders before the current run. Use this for final verification, not while debugging a failing run.

## Reading Results

Each run writes a small report bundle:

- `results.json`: machine-readable status, timings, errors, and artifact paths.
- `index.html`: human-readable run report.
- `generated-compose/docker-compose.generated.yml`: the exact environment used.
- `generated-compose/config/*`: generated ClickHouse configuration.
- `logs/*`: Docker Compose and service logs.
- `artifacts/<TestName>/*`: expanded SQL, actual CSVs, CLI output, and failure details.

When a test fails, start with `index.html` or `results.json`, then move to the per-test artifacts and service logs.

## Adding Tests

Start with a declarative test unless the scenario needs custom PowerShell logic.

1. Add `TestingSuite/Tests/<TestName>/TestDefinition.psd1`.
2. Add setup and verification SQL under `TestingSuite/Tests/<TestName>/Sql`.
3. Add expected CSV output under `TestingSuite/Tests/<TestName>/Expected`.
4. Run the test with a unique `-TestId`.
5. Inspect `.artifacts/TestResults/<test-id>`.
6. Run one final passing verification with `-CleanTestResults`.

Use `TestingSuite/README.md` as the detailed reference for supported resources, tokens, declarative step fields, JSON assertions, and custom test helpers.

Large debug-only scenarios should set `ExcludeFromRunAll = $true`. `LargeOnTimeBackupGc` is the reference for loading a public ClickHouse OnTime S3 dataset slice, optimizing it, validating full and no-op incremental backup behavior, and exercising explicit large S3 garbage collection; run it explicitly with long timeouts when debugging cleanup behavior.

