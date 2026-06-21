---
name: chobo-ui-tests
description: Run Chrome-based manual-QA style UI tests for ChoboWeb against a real ChoboServer, ClickHouse, and MinIO environment. Use when testing Chobo GUI flows such as first bootstrap without a dev token, cluster/storage setup, policy and schedule creation, backup/restore execution, detail pages, logs, audit, failure UX, semantic usability, and numbered screenshot artifact review.
---

# Chobo UI Tests

Use this skill to exercise Chobo from the perspective of a person using Chrome. Prefer semantic, user-facing checks over brittle exact copy, pixel positions, or DOM snapshots.

## Quick Start

From the repository root:

```powershell
$envInfo = .\.codex\skills\chobo-ui-tests\scripts\start-ui-env.ps1 -Scenario full
node .\.codex\skills\chobo-ui-tests\scripts\run-ui-scenario.mjs --env $envInfo.EnvFile --scenario full
.\.codex\skills\chobo-ui-tests\scripts\stop-ui-env.ps1 -EnvFile $envInfo.EnvFile
```

Use `-KeepEnvironment` on `start-ui-env.ps1` while debugging, and skip the stop script until inspection is done.

## Scenarios

Supported scenario names:

- `bootstrap`: real first install, token capture, sign-in, repeat-install rejection.
- `cluster`: bootstrap plus source/restore ClickHouse cluster setup.
- `storage`: bootstrap plus MinIO backup storage setup.
- `policy`: cluster/storage plus policy selector and retention setup.
- `schedule-edit`: policy plus schedule create/edit checks.
- `backup`: policy plus manual backup and backup details.
- `restore`: backup plus restore wizard, destructive confirmation handling, restore details, and restored-row verification.
- `details`: backup/restore details, related logs, and related audit.
- `logs-audit`: logs and audit screens after operational activity.
- `failure`: intentional bad connection values to check useful failure UX.
- `full`: default full journey through bootstrap, setup, policy, schedule, backup, schema browser, restore, logs/audit, and destructive confirmation checks.

Read `references/scenarios.md` before adjusting scenario behavior. Read `references/test-data.md` before changing form values. Read `references/semantic-qa-rubric.md` when judging whether a screen "works nicely."

## Required Artifacts

Every run must write artifacts under `.artifacts/TestResults/<ui-test-id>/ui/`:

- `screenshots/*.png`: numbered major-step screenshots such as `001-bootstrap-install-screen.png`.
- `screenshots/index.md`: numbered screenshot review index with route, step, and QA note.
- `report.md`: concise human-readable QA report.
- `result.json`: machine-readable scenario result.
- `console.log` and `network.log`: browser health evidence.

On failure, capture both `NNN-failure-current-screen.png` and, when possible, `NNN-failure-full-page.png`.

## Operating Rules

- Do not use `dev-static-access-token` or `static-test-token` for the bootstrap scenario.
- Use Chrome or Playwright's Chromium channel with a browser UI perspective.
- Use known-good values from `references/test-data.md` unless the user explicitly requests a variant.
- Prefer role/label-based interactions. Use text and DOM fallbacks only to keep the test resilient to UI copy changes.
- When labels share prefixes or contain option text, avoid fuzzy label fallbacks. Use exact accessible labels when available, or a stable local scope such as the second select in a known form only after confirming the surrounding screen.
- Prefer ChoboWeb in-app confirmation dialogs for destructive-action GUI coverage so screenshots can show the actual confirmation window. Assert the visible `role="dialog"`, screenshot it, then exercise both cancel and confirm when the flow supports it.
- Native browser confirmations are not visible in Playwright screenshots. Only use Playwright `dialog` handling for legacy/browser-native prompts, and document the missing screenshot evidence in `report.md`.
- If Docker Compose cleanup fails under the sandbox, rerun the stop script with escalation instead of leaving UI containers running.
- Verify data integrity with backend SQL/API checks when needed, but perform workflow checks through the browser.
- Keep Docker-heavy runs sequential. Do not run multiple Chobo UI environments unless the user explicitly asks.
