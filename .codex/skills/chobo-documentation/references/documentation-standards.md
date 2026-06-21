# Chobo Documentation Standards

## Audience and Tone

- User docs are for DBAs and operators responsible for ClickHouse data safety.
- Developer docs are for people changing, testing, or releasing Chobo.
- Use clear, concise language. Avoid marketing copy and avoid internal implementation trivia.
- Restore docs should feel steady and reassuring. Explain what will happen, what choices mean, and how to validate before touching production data.
- Prefer `you` over third-person phrasing like `a DBA wants`.

## User Docs Structure

Keep user docs under `docs/user/`:

- `README.md`: reading order and operational reference.
- `Installation.md`: Docker/binary setup, persistent data, ports, first token, preflight, upgrade/rollback, metadata DR.
- `Onboarding.md`: terms, first login, clusters, S3, first policy, schedule, setup validation.
- `PoliciesAndScheduling.md`: policy terms, GUI selector builder, include/exclude rules, retention, schedules, manual runs.
- `Backups.md`: how Chobo backs up ClickHouse, clusters/shards, schema/data behavior, monitoring, incrementals, cancellation.
- `Restores.md`: emergency runbook, GUI wizard, layouts, mappings, append, progress/history/details, validation, failure handling.
- `UsersAndAccessControl.md`, `LogsAndAudits.md`, `SchemaBrowser.md`, `Security.md`, `BackupLifecycle.md`, `Troubleshooting.md`.

Keep developer docs under `docs/developer/`:

- debugging/local compose setups;
- system tests;
- release workflow;
- Codex/development notes;
- internal test hooks, endpoint rewrites, local-only ports, debug credentials.

## Keep Internal/Test Details Out of User Docs

Do not put these in user/operator docs or README screenshots unless explicitly documenting local development:

- `EndpointRewrites`, `appsettings.Development.json`, `resources/debug`, `dev-static-access-token`, `static-test-token`.
- Test hooks such as `CHOBO_TEST_HOOKS_ENABLED`.
- Docker-compose-only hostnames or ports like `localhost:18111`.
- Compatibility notes like native ClickHouse port remapping unless the product docs intentionally expose it.

For user docs, say Chobo connects to ClickHouse through HTTP(S) and show explicit HTTP(S) ports such as `8123` when an example needs a port.

## GUI Screenshot Rules

Use visual assets that explain the real workflow.

Good screenshots:

- show populated tables and selected records;
- show successful or neutral states for normal workflow docs;
- show the actual control being explained;
- include enough sample data to make the UI understandable;
- are captured from a real ChoboWeb run when possible.

Avoid screenshots that are:

- empty/default states when explaining usage;
- failure/error states on introductory restore docs;
- cropped so tightly that the workflow context is lost;
- stale after UI or doc wording changes.

Preferred screenshot coverage:

- Policy docs: policy form, include/exclude selector rows, selected table preview, saved policy list.
- Restore docs: backup choice, destination/layout cards, `Single node`, `Redistribute`, table mappings, review, confirmation dialog, succeeded history/progress, details page.
- Schema docs and README: selected backup with database/table tree and visible `CREATE TABLE` SQL.
- Backup docs: successful list, detail page with table/shard context, logs/audit if relevant.

When screenshots are needed, use `chobo-ui-tests`:

```powershell
$envInfo = .\.codex\skills\chobo-ui-tests\scripts\start-ui-env.ps1 -Scenario full
node .\.codex\skills\chobo-ui-tests\scripts\run-ui-scenario.mjs --env $envInfo.EnvFile --scenario full
.\.codex\skills\chobo-ui-tests\scripts\stop-ui-env.ps1 -EnvFile $envInfo.EnvFile
```

Copy durable documentation screenshots into `docs/user/assets/<topic>/` with descriptive names. Do not link docs directly to `.artifacts/`.

## CLI and Sample Output Rules

- Verify commands against `ChoboCli/Commands/*.cs` and `ChoboCli/COMMANDS.md`.
- Include CLI examples for automation and runbooks, but do not let CLI dominate pages where the GUI is the primary user workflow.
- Include sample output when it helps users recognize success or diagnose errors.
- Match DTO shapes from `Chobo.Contracts`; do not invent fields.
- For logs/audit, remember results are paged when the API returns `PagedResultDto<T>`.
- Include required safety flags, such as `--confirm-destructive`, wherever the command implementation requires them.

## Restore Documentation Rules

Restore pages should prioritize confidence and safe action.

Must include:

- emergency runbook;
- how to choose a backup/recovery point;
- scratch restore guidance;
- target cluster and target table naming;
- layout choices: preserve, redistribute, single-node;
- source/target shard controls when relevant;
- append and schema mismatch warnings;
- confirmation behavior;
- progress/history/details screenshots;
- post-restore validation queries;
- partial-success and failure inspection steps.

For restore screenshots, prefer happy succeeded states. Use failure screenshots only in failure-handling sections.

## Policy Documentation Rules

Policy docs should make selectors understandable visually.

Must include:

- what a policy contains;
- GUI form screenshot with real source cluster/storage and selector rules;
- include/exclude ordering explanation;
- selected table preview screenshot;
- saved policy list screenshot;
- CLI selector JSON for reproducible runbooks;
- retention and failed-backup cleanup behavior;
- schedule linkage and manual run path.

## Security and Production Notes

Include:

- do not expose Chobo management publicly;
- persist `Chobo:DataDirectory`;
- protect and keep `CHOBO_ENCRYPTION_KEY_BASE64` stable;
- use dedicated ClickHouse and S3 identities;
- access tokens are broad operator credentials unless the product adds narrower roles;
- imports do not restore decrypted credentials and operators must re-enter credentials.

For ClickHouse cluster grants, show `ON CLUSTER '{cluster}'` as optional cluster-wide DDL, separate from single-node examples.

## Validation Checklist

After documentation edits:

1. Run a local Markdown link check for `README.md`, `ChoboCli/COMMANDS.md`, and `docs/**/*.md`.
2. Verify added PNG files exist and have dimensions.
3. Scan user docs for internal/test terms: `EndpointRewrites`, `appsettings.Development`, `resources/debug`, `dev-static`, `static-test-token`, `CHOBO_TEST`, `localhost:181`, test hooks.
4. Scan for stale paths after moves: `docs/assets`, `ProductionSetup.md`, `Restoring.md`, `DebuggingInstructions.MD`.
5. If screenshots were captured, stop any UI test environment afterward.