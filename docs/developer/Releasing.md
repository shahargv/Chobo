# Releasing Chobo

Chobo releases are built by GitHub Actions. The release version comes from the Git tag or manual workflow input; do not edit product/server version constants in code.

API, export, and SQLite schema versions remain explicit compatibility versions in `Chobo.Contracts/ChoboApi.cs`. Advance them only when the corresponding contract or schema changes.

## Pull Request CI

Every pull request runs:

- restore and `Release` solution build;
- unit tests with hang protection;
- smoke system tests: `SmokeCreateTables` and `ChoboCrudSmoke`.

Pull requests do not build Docker images and never push Docker images.

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
2. Create and push a semantic version tag:

   ```powershell
   git checkout master
   git pull
   git tag v1.2.3
   git push origin v1.2.3
   ```

3. GitHub Actions runs the release workflow.
4. Verify the GitHub Release contains:

   - `chobo-cli-win-x64.zip`
   - `chobo-cli-linux-x64.zip`
   - `chobo-server-win-x64.zip`
   - `chobo-server-linux-x64.zip`
   - `chobo-server-docker-v1.2.3.tar`
   - `chobo-cli-docker-v1.2.3.tar`
   - `SHA256SUMS.txt`

5. Verify Docker Hub tags:

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
