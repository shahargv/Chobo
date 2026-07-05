---
name: chobo-release
description: Use when preparing, validating, publishing, or troubleshooting a new Chobo release version, including version selection, release branch/tag checks, GitHub Actions release workflow dispatch, GitHub Release artifact verification, and Docker Hub tag verification.
---

# Chobo Release

Use `docs/developer/Releasing.md` and `.github/workflows/release.yml` as the source of truth. The release version is stamped by CI from the tag or manual workflow input; do not edit product/server version code just to release.

## Release Safety

Before doing anything that publishes externally, confirm the user explicitly asked to publish/release the version. Treat these as publishing actions:

- creating or pushing a `vX.Y.Z` tag;
- dispatching the `Release` workflow;
- rerunning a release workflow that pushes Docker images;
- editing an existing GitHub Release.

If the user asks to prepare a release but not publish it, stop after local validation and a clear checklist.

## Preflight

1. Check the working tree:

   ```powershell
   git status --short --branch
   ```

   Do not hide unrelated dirty files. If release-critical files are dirty, inspect them before proceeding.

2. Confirm the version is SemVer-like and has no leading `v` when passed to scripts or workflow inputs. Use a leading `v` only for Git tags.

3. Run the release version policy check before publishing:

   ```powershell
   .\scripts\Test-ReleaseVersionPolicy.ps1 -Version <version>
   ```

   Review the schema advisory every time. If it reports likely or ambiguous SQLite schema changes, notify the release owner before continuing. Do not publish until any required `ChoboApi.SchemaVersion` and release minor `Y` changes are resolved.

4. Confirm compatibility versions only if the release includes contract changes:

   - `ApiVersion`
   - `ExportVersion`
   - `SchemaVersion`

   These live in `Chobo.Contracts/ChoboApi.cs` and are not normal release-version numbers.

5. Confirm Docker Hub secrets exist before publishing through Actions:

   - `DOCKERHUB_USERNAME`
   - `DOCKERHUB_TOKEN`

## Local Validation

Run these from the repository root before publishing:

```powershell
dotnet restore Chobo.sln
dotnet build Chobo.sln -c Release --no-restore
dotnet test Chobo.Tests\Chobo.Tests.csproj -c Release --no-build -v minimal --blame-hang --blame-hang-timeout 30s
.\TestingSuite\TestManager.ps1 -TestId release-smoke-<version> -TestName SmokeCreateTables,ChoboCrudSmoke -GlobalTimeoutSeconds 1800 -TestTimeoutSeconds 300
.\TestingSuite\TestManager.ps1 -TestId release-import-export-<version> -TestName ImportExportRoundTrip -GlobalTimeoutSeconds 600 -TestTimeoutSeconds 300
.\scripts\Test-ReleaseVersionPolicy.ps1 -Version <version>
.\scripts\Test-UpgradeSamples.ps1 -Version <version>
.\scripts\Invoke-ReleaseDryRunRehearsal.ps1 -Version <same-schema-candidate> -SkipUpgradeSamples
.\scripts\Invoke-ReleaseDryRunRehearsal.ps1 -Version <schema-change-candidate> -SchemaChange -SkipUpgradeSamples
.\scripts\New-ReleaseDbSample.ps1 -Version <version>
.\scripts\Test-UpgradeSamples.ps1 -Version <version>
.\scripts\Build-Artifacts.ps1 -Configuration Release -Version <version>
```

Use the `chobo-system-tests` skill for system-test failures, log inspection, or full-suite debugging. Docker-heavy system tests should run sequentially.

Do not continue to publishing or release artifact build until the release policy, upgrade sample validation, import/export round trip, and dry-run rehearsal checks have passed. The dry-run rehearsal commands are intended for subagents: one checks a same-schema patch release path, and one checks a schema-change minor release path. They must not push tags, dispatch GitHub Actions, edit GitHub Releases, or push Docker images.

After `New-ReleaseDbSample.ps1` creates `.release/db-samples/<version>`, include that fixture in the release-ready commit before tagging. CI validates committed samples but does not generate or commit them.

Check the artifact output:

- `.artifacts/build/Release/chobo-cli-win-x64.zip`
- `.artifacts/build/Release/chobo-cli-linux-x64.zip`
- `.artifacts/build/Release/chobo-server-win-x64.zip`
- `.artifacts/build/Release/chobo-server-linux-x64.zip`
- `.artifacts/build/Release/SHA256SUMS.txt`

Optionally verify version stamping by inspecting `/api/v1/server/version` in a local server or checking assembly informational version in the produced binaries.

## Publish Paths

Prefer tag release for official releases:

```powershell
git checkout master
git pull
git tag v<version>
git push origin v<version>
```

Use manual workflow dispatch only when a maintainer explicitly wants to rebuild the release from a selected commit. The input version must be `<version>`, not `v<version>`.

The release workflow performs:

- release build and unit tests;
- full system test suite;
- binary artifact build;
- GitHub Release creation/update;
- Docker Hub publish to `shahargv/chobo`.

## Post-Publish Verification

After the GitHub Actions release workflow completes, verify:

- the workflow run passed all jobs;
- the GitHub Release for `v<version>` exists;
- release assets include all four zip files, both Docker image tar files, and `SHA256SUMS.txt`;
- Docker Hub tags exist:
  - `shahargv/chobo:server-v<version>`
  - `shahargv/chobo:server-latest`
  - `shahargv/chobo:cli-v<version>`
  - `shahargv/chobo:cli-latest`

If the workflow fails after partial publishing, inspect which publish steps completed before retrying. Prefer rerunning the failed workflow job only when it is clear that re-uploading release assets and repushing Docker tags is safe for that version.

