# Codex Development Notes

This repository includes Codex skills for repeatable Chobo workflows. Skills are short project guides that remind Codex how to follow repo-specific processes without rediscovering the workflow each time.

## Chobo System Tests

The skill is useful when asking Codex to:

- add or adjust a system test;
- run a named system test through `TestingSuite/TestManager.ps1`;
- inspect `.artifacts/TestResults/<test-id>` after a failure;
- debug ChoboServer, ChoboCli, ClickHouse, MinIO, or test-runner logs;
- choose the right declarative test shape for a backup, restore, audit, or failure-handling scenario.

### How Codex Should Work Here

Codex should treat `TestingSuite/README.md` as the detailed source of truth. The local skill keeps the high-level workflow close at hand:

- prefer declarative tests when possible;
- run tests through `TestingSuite/TestManager.ps1`;
- always pass a clear `-TestId`;
- use `-GlobalTimeoutSeconds` and `-TestTimeoutSeconds` for bounded runs;
- inspect `results.json`, `index.html`, generated Compose files, logs, and per-test artifacts;
- run Docker-heavy system tests sequentially;
- use `-CleanTestResults` only for the final verification run.

The skill does not replace the documentation. It is a compact operating note for Codex so project-specific habits stay consistent across sessions.

## Chobo Release

The release skill is useful when asking Codex to:

- prepare or validate a new Chobo release version;
- run release preflight checks before publishing;
- build and inspect zipped release artifacts and checksums;
- confirm version stamping behavior;
- choose between tag release and manual GitHub Actions workflow dispatch;
- verify GitHub Release assets and Docker Hub tags after publishing;
- troubleshoot failed release workflow runs.

Codex should treat `docs/Releasing.md` and `.github/workflows/release.yml` as the detailed sources of truth. The local skill keeps the release operating checklist close at hand:

- do not edit product/server version code just to release;
- keep API, export, and schema versions as explicit compatibility versions;
- confirm the user explicitly asked to publish before pushing tags, dispatching release workflows, or modifying GitHub Releases;
- run bounded local validation before publishing;
- use the `chobo-system-tests` skill for system-test failures;
- verify all GitHub Release assets and Docker Hub tags after the workflow completes.

## Location

The skills live at:

- `.codex/skills/chobo-system-tests/SKILL.md`
- `.codex/skills/chobo-release/SKILL.md`

If the system-test workflow changes, update both the detailed `TestingSuite/README.md` and the system-test skill summary. If the release workflow changes, update both `docs/Releasing.md` and the release skill summary. This keeps humans and Codex following the same process.
