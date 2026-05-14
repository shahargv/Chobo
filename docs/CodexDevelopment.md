# Codex Development Notes

This repository includes a Codex skill for Chobo system-test work. The skill is a short project guide that reminds Codex how to create, run, and inspect the Docker Compose based test suite without rediscovering the workflow each time.

The skill is useful when asking Codex to:

- add or adjust a system test;
- run a named system test through `TestingSuite/TestManager.ps1`;
- inspect `.artifacts/TestResults/<test-id>` after a failure;
- debug ChoboServer, ChoboCli, ClickHouse, MinIO, or test-runner logs;
- choose the right declarative test shape for a backup, restore, audit, or failure-handling scenario.

## How Codex Should Work Here

Codex should treat `TestingSuite/README.md` as the detailed source of truth. The local skill keeps the high-level workflow close at hand:

- prefer declarative tests when possible;
- run tests through `TestingSuite/TestManager.ps1`;
- always pass a clear `-TestId`;
- use `-GlobalTimeoutSeconds` and `-TestTimeoutSeconds` for bounded runs;
- inspect `results.json`, `index.html`, generated Compose files, logs, and per-test artifacts;
- run Docker-heavy system tests sequentially;
- use `-CleanTestResults` only for the final verification run.

The skill does not replace the documentation. It is a compact operating note for Codex so project-specific habits stay consistent across sessions.

## Location

The skill lives at `.codex/skills/chobo-system-tests/SKILL.md`.

If the system-test workflow changes, update both the detailed `TestingSuite/README.md` and the skill summary so humans and Codex keep following the same process.
