# Releasing Chobo

Chobo releases are built by GitHub Actions. The release version comes from the Git tag or manual workflow input; do not edit product/server version constants in code.

Release versions use `X.Y.Z`: `X` is the owner-selected major line, `Y` is the SQLite schema-change counter within that major line, and `Z` is the same-schema feature/patch counter. `ChoboApi.SchemaVersion` remains the internal monotonic SQLite compatibility integer. Advance API, export, and SQLite schema compatibility versions in `Chobo.Contracts/ChoboApi.cs` only when the corresponding contract or schema changes.

## Pull Request CI

Every pull request runs:

- restore and `Release` solution build;
- unit tests with hang protection;
- smoke system tests: `SmokeCreateTables` and `ChoboCrudSmoke`.

Pull requests do not build Docker images and never push Docker images.

Before publishing a release, run the local release validation checklist in the release skill. Release validation includes version-policy review, import/export coverage, upgrade sample validation, and dry-run release rehearsals. The large OnTime and large-table UI scenarios are no longer required release gates; keep them for targeted storage or UI investigations.

Use labels for exceptional cases:

- `skip-ci`: skips PR validation jobs for the pull request.
- `skip-system-tests`: keeps build and unit tests, but skips smoke system tests.

Use skip labels sparingly. Release workflows ignore these labels.

## Required Secrets

Configure these repository secrets before publishing a release:

- `DOCKERHUB_USERNAME`
- `DOCKERHUB_TOKEN`

Docker images are published to `docker.io/shahargv/chobo`.

## Release By Tag

1. Merge the release-ready commit to the default branch. This repository currently uses `master`.
2. Run local release validation:

   ```powershell
   dotnet restore Chobo.sln
   dotnet build Chobo.sln -c Release --no-restore
   dotnet test Chobo.Tests\Chobo.Tests.csproj -c Release --no-build -v minimal --blame-hang --blame-hang-timeout 30s
   .\TestingSuite\TestManager.ps1 -TestId release-smoke-1.2.3 -TestName SmokeCreateTables,ChoboCrudSmoke -GlobalTimeoutSeconds 1800 -TestTimeoutSeconds 300
   .\TestingSuite\TestManager.ps1 -TestId release-import-export-1.2.3 -TestName ImportExportRoundTrip -GlobalTimeoutSeconds 600 -TestTimeoutSeconds 300
   .\scripts\Test-ReleaseVersionPolicy.ps1 -Version 1.2.3
   .\scripts\Test-UpgradeSamples.ps1 -Version 1.2.3
   .\scripts\Invoke-ReleaseDryRunRehearsal.ps1 -Version <same-schema-candidate> -SkipUpgradeSamples
   .\scripts\Invoke-ReleaseDryRunRehearsal.ps1 -Version <schema-change-candidate> -SchemaChange -SkipUpgradeSamples
   .\scripts\New-ReleaseDbSample.ps1 -Version 1.2.3
   .\scripts\Test-UpgradeSamples.ps1 -Version 1.2.3
   .\scripts\Build-Artifacts.ps1 -Configuration Release -Version 1.2.3
   ```

   Review and report the `Test-ReleaseVersionPolicy.ps1` schema advisory before publishing. If it finds likely or ambiguous SQLite schema changes, stop until the release owner confirms whether `ChoboApi.SchemaVersion` and release minor `Y` are correct.

   The dry-run rehearsal commands are intended to be run by subagents before the real release. They validate the procedure without pushing tags, dispatching GitHub Actions, editing GitHub Releases, pushing Docker images, or making durable repository changes.

3. Commit the generated `.release/db-samples/1.2.3` fixture with the release-ready change.
4. Create and push a semantic version tag:

   ```powershell
   git checkout master
   git pull
   git tag v1.2.3
   git push origin v1.2.3
   ```

5. GitHub Actions runs the release workflow.
6. Verify the GitHub Release contains:

   - `chobo-cli-win-x64.zip`
   - `chobo-cli-linux-x64.zip`
   - `chobo-server-win-x64.zip`
   - `chobo-server-linux-x64.zip`
   - `chobo-server-docker-v1.2.3.tar`
   - `chobo-cli-docker-v1.2.3.tar`
   - `SHA256SUMS.txt`

7. Verify Docker Hub tags:

   - `shahargv/chobo:server-v1.2.3`
   - `shahargv/chobo:server-latest`
   - `shahargv/chobo:cli-v1.2.3`
   - `shahargv/chobo:cli-latest`

## Manual Release

Use the `Release` workflow's manual dispatch when a release needs to be rebuilt from the selected commit. Enter the version without the leading `v`, for example `1.2.3`.

The workflow creates or updates the GitHub Release for tag `v1.2.3`, uploads zipped binary artifacts, Docker image tar files, and checksums, and publishes the Docker Hub images.

## Version Stamping

Local builds default to `0.0.0-dev`.

Release builds pass:

```text
/p:Version=X.Y.Z /p:InformationalVersion=X.Y.Z+<commit>
```

`/api/v1/server/version`, export envelopes, and stored schema product version use the stamped assembly informational version. This keeps release version management in CI instead of requiring a source change for every release.

## Upgrade Samples

Release samples live under `.release/db-samples/X.Y.Z` and are committed to the repository. Each sample contains a self-contained `chobo.db`, `config-export.json`, `data-export.json`, and `sample-manifest.json`.

Generate a sample for the release candidate after validation and before tagging:

```powershell
.\scripts\New-ReleaseDbSample.ps1 -Version 1.2.3
```

Backfill a previous release sample from a local tag when needed:

```powershell
.\scripts\New-ReleaseDbSample.ps1 -Version 1.2.3 -FromTag v1.2.3
```

Validate upgrade compatibility against the latest committed prior-minor sample:

```powershell
.\scripts\Test-UpgradeSamples.ps1 -Version 1.2.3
```

