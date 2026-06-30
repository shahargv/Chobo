# Entity-Perspective Restore Implementation Tasks

## Contracts and API

- [x] Add restore plan/source DTOs.
- [x] Add restore plan endpoint.
- [x] Add initiate-from-plan/policy endpoint.
- [x] Validate shard source overrides.
- [x] Preserve legacy backup-first restore API.

## Server planning and execution

- [x] Build shared entity restore planner.
- [x] Implement policy latest defaults.
- [x] Implement anchor-backup closest-earlier defaults.
- [x] Use selected shard backup storage target/path during execution.
- [x] Include queue preview and redacted RESTORE statements.
- [x] Add audit details for per-shard backup sources.

## Web

- [x] Replace wizard entry with policy/entity-first flow.
- [x] Preserve backup-detail launch defaults.
- [x] Keep custom schema SQL editing on step 3.
- [x] Add table Details modal for all-shards/per-shard source choice.
- [x] Show final queue, restore statements, CLI command, and JSON payload.

## CLI

- [x] Add `restore plan`.
- [x] Add `restore initiate-from-plan --file`.
- [x] Add `restore initiate-from-policy`.
- [x] Keep existing `restore initiate --backup-id`.

## Migration/import

- [x] Avoid schema version bump.
- [x] Reuse existing restore table/shard foreign keys if possible.
- [x] Ensure current v1 data/config import remains compatible.

## Tests and verification

- [x] Add unit/API tests for defaults and validation.
- [x] Add web tests for wizard choices and summary.
- [x] Add system tests for entity policy restore CLI path; mixed shard-source behavior is covered by API tests and restore execution changes.
- [x] Run bounded backend tests.
- [x] Run web typecheck/tests.
- [x] Run targeted system tests.
## Implementation plan

### Summary

Replace the GUI restore wizard with an entity-perspective restore flow, while keeping the existing backend restore engine and current `restore initiate --backup-id` API/CLI path compatible.

Core model:

- `Restores.BackupId` remains the anchor backup used for schema/table selection.
- `RestoreTables.BackupTableId` remains the anchor table/schema.
- `RestoreTableShards.BackupTableShardId` becomes the explicit actual source shard backup.
- No schema version bump, even if schema changes are needed; update the current v1 baseline/import/export compatibility in place.

### Key changes

- Add a restore preview/plan endpoint used by both GUI and CLI.
- Accept `policyId` or `anchorBackupId`, target cluster/layout, table mappings, shard source overrides, and restore settings.
- Return anchor backup/schema, selected tables, available shard backup candidates, default shard choices, final planned restore shards, queue rows, and generated CLI JSON.
- Policy start defaults each shard to the latest compatible successful backup for that policy/table/shard.
- Backup start defaults each shard to that backup, or the closest earlier compatible backup that contains that shard.
- Add entity restore initiation from the preview shape.
- Preserve schema SQL override editing and audit hashing.
- Create/validate target table using anchor table schema.
- Run each shard restore from selected `BackupTableShardId`.
- Resolve restore storage target/path from the selected shard backup.

### GUI and CLI

- GUI Step 1 chooses policy, with backup-detail launch preselecting a specific anchor backup.
- GUI Step 2 chooses anchor backup/schema backup and target/layout.
- GUI Step 3 keeps destination name, backup type/mode, append, schema mismatch, and custom schema SQL.
- Table Details opens a modal supporting one backup for all shards or per-shard backup choice.
- Summary shows final table/shard plan, final queue rows, redacted RESTORE statements, equivalent CLI command, and JSON payload.
- CLI adds `restore plan`, `restore initiate-from-plan --file`, and `restore initiate-from-policy`.
- Existing `restore initiate --backup-id` remains supported.

### Migration and import

- Do not bump `ChoboApi.SchemaVersion`.
- Prefer no persisted schema changes; reuse existing restore table/shard foreign keys.
- If new persisted columns/indexes become unavoidable, update current v1 compatibility in place.
- Ensure current v1 data/config imports remain compatible and imported restore history is inspectable, not re-queued.

### Tests

- Unit/API: defaults, validation, queue preview, redacted statements, legacy compatibility, v1 import compatibility.
- Web: policy-first defaults, backup-first defaults, Details modal all-shards/per-shard choices, schema SQL editing, summary CLI/JSON/queue rendering.
- System: mixed shard source restore, backup-start fallback, schema-only anchor with data sources, negative incompatible/unavailable source.
- Verification: bounded backend tests, web typecheck/tests, targeted system tests.

### Review workflow

- After implementation and local verification, start a clean-context subagent to validate the feature against this plan.
- Start two additional clean-context CR subagents using only this plan: one regular code review, one design/product-impact review.
- Aggregate all subagent findings.
- Implement accepted CR comments and rerun affected verification.
## Verification log

- `npm run typecheck` passed.
- `npm test` passed: 10 files, 34 tests.
- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --filter "Entity_restore_plan|Restore_redistribute_can_limit_target_shards_to_one_shard" --blame-hang --blame-hang-timeout 30s` passed: 4 tests.
- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --filter "Data_import_export_round_trips_operational_metadata_without_audits_logs_or_credentials|In_flight_import" --blame-hang --blame-hang-timeout 30s` passed: 1 test.
- `.\TestingSuite\TestManager.ps1 -TestName BackupRestoreSharded -TestTimeoutSeconds 240` passed.
- `.\TestingSuite\TestManager.ps1 -TestName IncrementalBackupSharded -TestTimeoutSeconds 300` passed with the new entity restore CLI path.
- `git diff --check` passed.
## Review aggregation

Accepted and implemented:

- Fixed schema-only anchor planning/initiation so explicit compatible data shard sources create restore shard rows and execute from the selected backup shard.
- Fixed generated CLI replay JSON so it clears selector/target shortcut fields when explicit table mappings are emitted.
- Made `restore initiate-from-plan --file` accept either the raw request JSON or a saved full `restore plan` response containing `cliJson`.
- Moved shard-source nested validation into the shared table-mapping validator.
- Added summary source-backup visibility for each queue row.
- Added service-level schema SQL override validation during plan generation.
- Added backend tests for CLI replay JSON/initiation and schema-only anchor with data shard source.

Not fully expanded in this pass:

- Candidate lists still focus on restorable candidates plus schema-incompatible reasons; showing every deleted/failed candidate as unavailable would require a broader candidate DTO/status expansion.
- Web still follows the requested wizard sequence of policy then anchor backup/schema backup. CLI supports policy-only latest-default planning.

## Final verification and playground update - 2026-06-30

- Added support for shard source candidates from backups with status `Succeeded` or `PartiallySucceeded`; selected shard rows still require a succeeded shard and validation rejects incompatible/deleted/wrong-policy sources.
- Relaxed table-mapping validation so an empty `shardSources` list means “use default shard source choices,” matching generated web payloads and service behavior.
- Refreshed OpenAPI/generated TypeScript after adding `backupStatus` to shard backup candidates.
- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --filter "Entity_restore_plan" --blame-hang --blame-hang-timeout 30s` passed: 6 tests.
- `npm run typecheck` passed.
- `npm test` passed: 10 files, 34 tests.
- Full UI scenario passed with `.codex\skills\chobo-ui-tests\scripts\run-ui-scenario.mjs --scenario full` against `ui-20260630-070707-d16de68c`.
- Full system suite passed: `TestingSuite\TestManager.ps1 -TestId codex-entity-restore-full-20260630-0710 -GlobalTimeoutSeconds 3600 -TestTimeoutSeconds 300 -RunAllConcurrency 1` ran 20/20 tests successfully.
- Started preserved sharded playground: `codex-entity-restore-playground-20260630-0742`; exposed ChoboServer at `http://127.0.0.1:56665` with token `static-test-token` via local proxy container `chobo-entity-restore-playground-proxy`.
- Added playground policy `playground-sharded-entity-restore` (`3d868c56-369e-44d5-b8c8-80b6681c29ad`) and two policy full backups (`74903245-bc6f-4f19-82d3-7ccbae4e649e`, `32b1ba8d-d064-4ba5-8aed-83f51614d796`).
- Verified policy-first restore plan through the playground URL: 1 table, 2 shard queue rows, 4 shard backup candidates, and generated CLI JSON.

## GUI feedback fix - 2026-06-30

- Fixed backup row selection so choosing a backup no longer silently changes the policy filter and hides other rows.
- Fixed empty selected-policy state so the policy dropdown remains visible and the user can switch back to another policy or All policies without refreshing.
- Renamed the table action/modal affordances from generic Details/schema wording to explicit shard source wording: `Schema / shards`, `Shard sources`, `Shard backup sources`, and `Backup for all shards`.
- Added web regression test coverage for empty selected policies and updated shard-source discoverability assertions.
- Verified `npm run typecheck` and `npm test` after the GUI fixes.
- Started updated Vite UI for the preserved playground at `http://127.0.0.1:5173`; API proxy is available on `http://127.0.0.1:8080`, with the original server proxy still on `http://127.0.0.1:56665`.

## GUI feedback fix 2 - 2026-06-30

- Restored the Scope table column header to `Advanced`; defaults remain valid without opening Advanced.
- Added a global `Restore to date` picker on the Scope step. Selecting a date applies the latest compatible shard backup on or before that date across selected tables; clearing it returns to latest defaults.
- Replaced the shard-source modal's nested searchable `DataTable` with a compact plain shard-source table to avoid filter/search controls inside the dialog.
- Added a higher-specificity width override for the table details dialog so shard-source controls fit cleanly.
- Verified the rendered playground modal: 920px dialog, no nested grid toolbar in the shard-source panel.
- Verified `npm run typecheck` and `npm test` after the UI/layout fixes.

## Backend unit verification - 2026-06-30

- Ran full backend unit suite: `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s`.
- Initial run found stale `Manual_backup_metadata_is_persisted_and_mapped` assertion expecting escaped JSON text in `ManualRequestJson`.
- Updated the test to deserialize `ManualBackupRequest` and assert the persisted `ClusterId` structurally.
- Rerun passed: 231/231 tests.

## GUI feedback fix 3 - 2026-06-30

- Fixed the Scope-step `Restore to date` control so it is a typeable `YYYY-MM-DD` text field and remains usable while the restore plan is loading.
- The typed restore date is retained and applied once shard backup candidates finish loading.
- Fixed the table `Details` modal so data-backed tables always show the shard backup source area, including loading/error/empty states, instead of hiding it when the plan response is still pending.
- Added web tests for the loading-state date picker and Details modal shard-source visibility.

## GUI feedback fix 4 - 2026-06-30

- Removed the Source backup Policy selector and Policy table column/filter; Source backup now uses a date window with a typeable `From date` field and quick presets for 12h, 24h, 3 days, and 1 week. The default is last 3 days.
- Kept `Restore to date` editable even before table selection so the field can always be typed into on Scope.
- Fixed Details for anchor-only/manual backups by showing the selected anchor backup's shard table instead of the dead `Choose a target and table...` fallback.
- Added component tests for the Source backup date filter and anchor-only Details shard table.
- Added and ran a real Chrome smoke under `.artifacts/TestResults/entity-restore-browser-smoke/` covering source date filters, manual single-source Details, policy-backed sharded Details, and typeable restore date.

## GUI feedback fix 5 - 2026-06-30

- Fixed Preserve layout validation for policy-backed entity restore when all source shards are selected and the target cluster has a different shard count. The wizard now treats 2-source to 3-target Preserve as invalid and auto-switches to Redistribute before restore planning.
- Reproduced the reported failed plan shape against the live playground by updating the Chrome smoke to use the `restore3` target for the policy-backed sharded backup.
- Reran `npm run typecheck`, `npm test`, and the real Chrome smoke. The smoke now asserts Redistribute is shown before opening Details and verifies the shard-source choices load.

## GUI feedback fix 6 - 2026-06-30

- Changed generated restore SQL previews to preserve the actual S3/path destination and redact only credential positions with `REDACTED`.
- Added backend coverage that the entity restore plan queue statement contains the real shard storage path, contains `REDACTED`, and no longer contains the old `<storage-path:redacted>` placeholder.
- Fixed restore review responsive behavior by moving the impact summary below the wizard earlier and keeping final queue/CLI overflow inside local containers instead of creating page-level horizontal scroll.
- Extended the Chrome smoke to open Review at 1270px and assert the document has no horizontal overflow.
- Reran focused backend test, `npm run typecheck`, `npm test`, and real Chrome restore wizard smoke.

## GUI feedback fix 7 - 2026-06-30

- Extracted restore SQL construction into `ClickHouseRestoreSqlBuilder` and routed actual ClickHouse restore execution through it.
- Changed entity restore plan previews to resolve the actual storage destination through the selected shard backup's storage provider, then redact only destination credentials as `REDACTED`.
- Fixed plan queue restore statements to use the selected target database/table from the row mapping instead of a guessed/default destination.
- Added backend regression coverage for visible storage paths, credential-only redaction, and custom target table names in plan SQL previews.
- Ran full backend unit suite after the final assertion: `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s` passed 231/231.
