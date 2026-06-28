# Storage Provider Abstraction Refactor

## Summary

Refactor backup storage so adding Azure Blob or NFS later is mostly additive provider work, not changes across backup/restore, metadata, cleanup, CLI, GUI, or SQLite schema. Implement the framework with S3 as the only provider for now. No public API backward compatibility is required; only import of current config/data exports must keep working.

## Core Architecture

- Replace S3-shaped persistence with provider-neutral storage:
  - provider key string, e.g. `s3`
  - `SettingsJson` for non-secret settings
  - `SecretsJson` for encrypted secrets
  - existing identity/name/delete/timestamp fields remain
- Rename `S3Path` to `StoragePath` everywhere in current code, contracts, manifests, exports, DTOs, UI, CLI, logs, tests, and messages.
- Add a provider registry/factory. Each provider owns:
  - settings/secrets parsing, validation, normalization, sanitized projection
  - secret encryption/decryption conventions
  - connection testing
  - metadata object operations: write/read/list/delete
  - directory/prefix cleanup operations
  - backup size/path listing operations
  - ClickHouse backup/restore destination SQL, including incremental `base_backup`
  - sensitive values for SQL/log redaction
- Move current S3 URL building, S3 object operations, S3 validation, and ClickHouse `S3(...)` destination building into `S3StorageProvider`.
- Refactor `ClickHouseAdapter` so it asks the provider for destination fragments and no longer knows about S3.

## Storage Consumers

- Manifest storage:
  - `BackupStorageManifestService` writes, reads, scans, and validates manifests through the resolved provider.
  - Required storage paths are provider-neutral `StoragePath` values.
- Metadata recovery:
  - scan/list/read behavior goes through provider object listing.
  - missing-path validation uses provider `ListObjects` or equivalent.
- Backup completion:
  - backup size measurement uses provider path listing.
- Cleanup and garbage collection:
  - `BackupCleanupService` deletes backup `StoragePath` directories/prefixes through the provider.
  - manifest object deletion also goes through the provider.
  - `BackupsGarbageCollectorBackgroundService` remains orchestration only and does not know provider details.
- Target connection tests:
  - routed through provider-specific test behavior.
- Import/export:
  - new exports use provider-neutral target/settings/secrets and `StoragePath`.
  - import converts current S3-shaped exports into the new provider model.
- SQLite self-backup:
  - remains out of scope; it is a local maintenance backup, not a configured backup target operation.

## API, CLI, GUI

- Keep generic target endpoints as the provider-neutral core API, and add a strongly typed S3 facade used by CLI and GUI:
  - `POST /api/v1/targets`
  - `PUT /api/v1/targets/{id}`
  - request: `name`, `type`, `settings`, optional `secrets`, `updateSecrets`
  - response: `id`, `name`, `type`, sanitized `settings`, `secretFields`, timestamps
  - `POST /api/v1/targets/s3` and `PUT /api/v1/targets/{id}/s3` accept strongly typed S3 settings/secrets and delegate to the generic service
  - CLI `targets add-s3` / `update-s3` and the GUI S3 form use the typed S3 facade, not hand-built generic JSON
- CLI target commands use the strongly typed S3 facade for S3 CRUD while core server logic still normalizes through the provider registry.
- GUI target page starts with storage type selection.
  - Only S3 is available now.
  - S3 form is isolated behind provider-specific UI config and submits through the typed S3 facade.

## Tracking And Review Tasks

- First implementation task: create `.codex/state/storage-abstraction-plan.md` with this plan and a detailed checklist.
- Keep the checklist updated during implementation.
- Before declaring implementation done:
  - create a subagent to compare code against that plan file
  - report all deviations and confirm they are intentional.
- Final extensibility review:
  - create one subagent to analyze adding NFS
  - create one subagent to analyze adding Azure Blob
  - fix missing abstractions before final tests.
- Code review:
  - one subagent reviews design and implementation
  - one subagent critically checks missing pieces, edge cases, tests, GUI, CLI, import/export, and failure paths.

## Detailed Task List

- [x] 1. Create `.codex/state/storage-abstraction-plan.md`.
- [x] 2. Inventory all S3-specific symbols, routes, DTOs, path fields, logs, tests, and docs.
- [x] 3. Define provider-neutral target contracts plus strongly typed S3 facade contracts.
- [x] 4. Define S3 settings/secrets under the generic model.
- [x] 5. Change target persistence to provider key plus settings/secrets JSON.
- [x] 6. Add provider registry and provider interface.
- [x] 7. Implement `S3StorageProvider`.
- [x] 8. Refactor target application service to use provider registry and expose typed S3 facade methods.
- [x] 9. Refactor metadata object operations to resolve providers.
- [x] 10. Refactor manifest write/read/scan/recovery to use provider operations.
- [x] 11. Refactor backup size measurement to use provider path listing.
- [x] 12. Refactor backup cleanup and GC deletion paths to use provider delete operations.
- [x] 13. Refactor `ClickHouseAdapter` backup/restore destination creation to provider SQL fragments.
- [x] 14. Rename `S3Path` to `StoragePath` across entities, contracts, exports, manifests, validators, UI, CLI, tests, and logs.
- [x] 15. Update import conversion for current config/data exports.
- [x] 16. Update new export/manifests to emit provider-neutral fields only.
- [x] 17. Update CLI storage command abstraction to call the typed S3 facade for S3 verbs.
- [x] 18. Update GUI storage type selection and S3 provider form to call the typed S3 facade.
- [x] 19. Refresh OpenAPI and generated TypeScript.
- [x] 20. Update unit, system, and GUI test expectations.
- [x] 21. Run design/code-review subagents and fix findings.
- [x] 22. Run NFS/Azure extensibility subagents and fix abstraction gaps.
- [x] 23. Run full unit tests, full system tests, and full Web GUI tests. Unit tests, Web GUI full scenario, and full system suite passed. Earlier high-concurrency full system run hit Docker memory pressure; final low-concurrency full system run passed.
- [x] 24. Run the tracking-file verification subagent and report intentional deviations.

## Test Plan

- Full unit test suite.
- Full system test suite.
- Full Web GUI test suite.
- Specific coverage for:
  - current export import into new provider model
  - S3 target CRUD through generic API
  - S3 ClickHouse backup/restore SQL equivalence
  - manifest write/read/scan/recovery through provider abstraction
  - required storage path validation through provider abstraction
  - backup size measurement through provider abstraction
  - cleanup/GC directory and manifest deletion through provider abstraction
  - CLI target commands using generic plumbing
  - GUI storage type selection with S3-only provider.

## Assumptions

- First implementation includes only S3 as a working provider.
- Azure Blob and NFS are abstraction validation targets, not implemented yet.
- User-visible product behavior remains unchanged.
- Clean new names are preferred; old S3-shaped names survive only inside import conversion.
- SQLite self-backup is not part of this storage target abstraction.

## Final Review Notes

- Initial review findings on legacy `s3Path` import were fixed with compatibility fallback fields for data exports and storage manifests. These fields are intentional import/read compatibility only; new exports/manifests continue writing provider-neutral `storagePath` and settings.
- Initial review findings on partial S3 credential updates were fixed by requiring S3 access key and secret key together, preserving credentials when both are omitted on update, and adding unit coverage.
- Target storage type is immutable on update to avoid interpreting old `StoragePath` records through a different provider.
- Manifest recovery now requires scan target type to match manifest target type before copying scan-target secrets.
- Generated TypeScript now treats provider target `settings` as general JSON and marks legacy compatibility fields optional.
- Intentional deviations: GUI provider UI is still S3-only in one page because only S3 is implemented now; adding NFS/Azure will still require adding typed facade contracts, CLI verbs, and GUI form branches. NFS support assumes ChoboServer has maintenance access to the same mounted storage, or a future maintenance-access abstraction will be needed. Provider-owned failure classification remains a future improvement for Azure/NFS.