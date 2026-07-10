# Password-Protected Backups Tracking

## Goal

Add policy-controlled ClickHouse password-protected and compressed ZIP backups while preserving restore, incremental, import/export, metadata-recovery, retention, and garbage-collection behavior. Passwords are encrypted with Chobo AES keys and stored per backup table-shard. Missing AES keys must be visible and must make only the affected data unusable; they must not prevent metadata import or garbage collection. Compression must also work and be tested independently without encryption.

## Status Legend

- `[ ]` pending
- `[~]` in progress
- `[x]` implemented and verified
- `[!]` blocked, with the blocker and owner recorded beside the task

Every completed task must include implementation or test evidence. Review findings and their resolutions are tracked in the final gates.

## Locked Decisions

- [ ] Encryption and compression are independent optional policy features. New and existing policies default to password mode `None`, `CompressionMethod = null`, and `CompressionLevel = null`.
- [ ] A policy may enable neither feature, encryption only, compression only, or both. Neither feature is required for schema-and-data policies.
- [ ] Add `BackupPasswordMode`: `None`, `Constant`, and `GeneratedPerTableShard`.
- [ ] Generate passwords with a cryptographically secure RNG, exactly 20 characters from `A-Z`, `a-z`, `0-9`, and `!@#$%^&*()`.
- [ ] Protection is configured only through a policy. Direct manual backups without a policy remain unprotected.
- [ ] Protected full backups and full fallbacks use the policy's current setting. Incremental table-shards inherit the selected full parent's password and protection state so ClickHouse can use `use_same_password_for_base_backup=true`.
- [ ] A constant-password change applies to new full chains and full fallbacks. Existing incremental chains keep the parent chain's password.
- [ ] Password plaintext is write-only and internal. Normal API, CLI, GUI, export, manifest, audit, and log surfaces never reveal it.
- [ ] Encrypted or compressed ClickHouse destinations use `.zip`; existing unencrypted-and-uncompressed directory-style paths remain unchanged.
- [ ] Add first-class optional policy compression settings: `CompressionMethod` plus optional `CompressionLevel`.
- [ ] Support ClickHouse ZIP methods `Store`, `Deflate`, `Bzip2`, `Lzma`, `Zstd`, and `Xz`, rendered to ClickHouse as lowercase setting values. A compression level requires a method; `Store` rejects a level because it performs no compression.
- [ ] Any effective compression method creates a `.zip` archive even when password mode is `None`. Encryption and compression may be enabled independently or together.
- [ ] Compression settings are snapshotted in the backup's existing effective ClickHouse settings so later policy changes do not alter historical metadata.
- [ ] Imported encrypted values and key IDs are preserved even when the referenced AES key is absent or malformed.
- [ ] Missing keys are resolved dynamically: copying/restoring a valid key file later makes affected policies/backups usable without changing database rows or re-importing data.
- [ ] SQLite schema increases from 1 to 2. API and export envelope versions stay at 1 because new export fields are optional and schema-v1 payloads remain accepted.
- [ ] Schema-only policies cannot enable backup password protection.

## 1. Public Contracts and Secret Boundaries

- [ ] Add the password-mode enum to `Chobo.Contracts` with string JSON serialization.
- [ ] Extend policy writes with `passwordMode` and write-only `backupPassword`.
- [ ] Extend policy writes/responses/exports with nullable `compressionMethod` and `compressionLevel`.
- [ ] Extend policy responses with `passwordMode`, `hasConfiguredPassword`, and `passwordKeyAvailable`; never return plaintext or ciphertext.
- [ ] Extend backup summaries with an aggregate encryption state: `Unencrypted`, `EncryptedKeyAvailable`, or `EncryptedMissingKey`.
- [ ] Expose the backup's effective compression method/level, derived from its snapshotted effective settings, without conflating compression with encryption status.
- [ ] Extend table-shard details with `isPasswordProtected`, `passwordKeyId`, and `passwordKeyAvailable`. The AES key ID is operational metadata and may be shown; ciphertext is not returned.
- [ ] Define aggregate behavior for mixed backups: if any protected shard has a missing/invalid key, the backup summary is `EncryptedMissingKey`; otherwise any protected shard makes it `EncryptedKeyAvailable`.
- [ ] Extend policy exports with password mode, encrypted constant password, and its key ID.
- [ ] Extend table-shard exports and storage manifests with optional encrypted backup password and key ID.
- [ ] Add an import-result DTO containing imported counts, distinct missing/invalid AES key IDs, affected policy count, affected shard count, and warning text.
- [ ] Keep old schema-v1 JSON and manifests compatible by defaulting absent password fields to unprotected.
- [ ] Refresh Swagger/OpenAPI and generated TypeScript after server contracts are complete; inspect enum casing and all client call sites.

## 2. AES Key Repository and Availability Cache

- [ ] Extend `IAesKeyRepository` with single and bulk availability checks and an explicit refresh operation; consumers must not infer availability by attempting unrelated decryptions.
- [ ] Replace per-lookup file reads with a thread-safe in-memory snapshot keyed by AES key ID.
- [ ] Load and validate key material once per snapshot; distinguish `Available`, `Missing`, and `Invalid` so malformed files are treated as unavailable with a useful diagnostic.
- [ ] Update the cache immediately when Chobo creates a new key.
- [ ] Watch the AES key directory for create/change/delete/rename events and invalidate or refresh affected entries.
- [ ] Add a short periodic rescan fallback for missed filesystem-watcher events, plus explicit refresh from user-triggered API/GUI refresh paths. Restored key files must become visible promptly without restart.
- [ ] Keep availability and bulk lookup O(distinct key IDs), not O(backups × shards), and never read the same key file once per row.
- [ ] Make cancellation, concurrent first load, concurrent refresh, key replacement, deletion, malformed content, and directory-not-yet-created behavior safe.
- [ ] Ensure cached key bytes are not logged, serialized, exported, or returned from availability APIs.
- [ ] Add unit tests proving repeated decrypt/availability calls use the cache, filesystem changes invalidate it, restored keys become available, deleted/invalid keys become unavailable, and concurrent access performs a bounded number of reads.

## 3. Policy Lifecycle, CLI, GUI, and Audit

- [ ] Add policy entity fields for mode, encrypted constant password, and key ID.
- [ ] Add nullable policy entity fields for compression method and compression level.
- [ ] Encrypt constants before persistence; never store plaintext.
- [ ] Validate a constant as non-empty and at most 4096 characters without trimming or altering its value.
- [ ] On create, require a password for constant mode and reject a password for other modes.
- [ ] On update, omit the password to retain an existing constant; supply it to replace; switching away clears the policy secret; switching to constant requires a supplied or already-valid constant.
- [ ] If a policy constant references a missing/invalid key, show the condition in policy DTO/CLI/GUI and require replacement or key restoration before starting a new protected full backup.
- [ ] Keep policy audits limited to mode and configured/available booleans; never include password or ciphertext.
- [ ] Add CLI policy options `--password-mode none|constant|generated-per-table-shard` and `--backup-password`; update help and `ChoboCli/COMMANDS.md`.
- [ ] Add GUI mode selection and a write-only password input. Editing an available constant explains that blank preserves it; an unavailable constant prompts the operator to restore the key or provide a replacement.
- [ ] Present password protection and compression as two separate optional sections in the policy create/edit GUI; both sections default to disabled and must be explicitly enabled by the operator.
- [ ] Label the controls and helper text clearly: `Password protection (optional)` and `Compression (optional)`. Explain that leaving them disabled preserves the normal unencrypted, unarchived backup format.
- [ ] Add GUI compression controls with an optional method selector and optional numeric level. The disabled state clears/hides the method and level controls. Explain that compression produces ZIP archives and can increase CPU/runtime for data that ClickHouse already compresses internally.
- [ ] When editing a policy, faithfully show whether each optional feature is enabled. Saving unrelated edits must not silently enable, disable, replace, or clear either feature.
- [ ] Add CLI policy options `--compression-method store|deflate|bzip2|lzma|zstd|xz` and `--compression-level <integer>`.
- [ ] Reject `compression_level` without a first-class method, reject a level with `Store`, and reject policy advanced-settings entries named `compression_method` or `compression_level` when the equivalent first-class field is present.
- [ ] Preserve compatibility for compression settings inherited from cluster advanced settings or supplied as final manual-operation settings. Existing precedence remains cluster -> policy -> manual operation; first-class policy compression occupies the policy layer.
- [ ] Add API, CLI, and GUI tests for create/update/preserve/replace/clear, invalid combinations, schema-only rejection, missing policy key status, and secret-safe output.
- [ ] Add GUI tests for default-both-disabled state, optional labels/helper text, enabling each feature independently, enabling both, disabling either feature, and preserving optional settings during unrelated policy edits.

## 4. SQLite Schema Version 2

- [ ] Increment `ChoboApi.SchemaVersion` from 1 to 2.
- [ ] Add `BackupPolicies` columns for password mode, encrypted constant password, and constant-password key ID.
- [ ] Add nullable `BackupPolicies` columns for compression method and compression level in the same schema-v2 migration.
- [ ] Add `BackupTableShards` columns for encrypted backup password and password key ID.
- [ ] Add a new schema-v2 EF migration; do not rewrite the published schema-v1 baseline.
- [ ] Add a transactional v1-to-v2 custom upgrade step with default `None` for existing policies and null password fields for existing shards.
- [ ] Advance `SchemaState` only after all schema work succeeds; make restart/retry behavior safe.
- [ ] Add fresh-v2, populated-v1 upgrade, failed-upgrade rollback/retry, preserved-history, and newer-schema rejection tests.
- [ ] Validate release-version policy against `v1.1.0`, the next minor line required for schema v2 after `v1.0.x`.

## 5. Password Assignment and ZIP Archive Paths

- [ ] Add a focused password-generator abstraction using `RandomNumberGenerator` and the exact 72-character alphabet.
- [ ] For constant full/fallback shards, decrypt the policy constant and independently encrypt it into every shard row.
- [ ] For generated full/fallback shards, generate and encrypt a distinct password for every shard row.
- [ ] For incremental shards with a usable protected parent, decrypt the parent's password and re-encrypt the same plaintext into the child row.
- [ ] For incremental shards with an unprotected parent, keep the child unprotected even if the policy was changed later.
- [ ] Persist password ciphertext/key ID before queueing or submitting ClickHouse work.
- [ ] Never regenerate or replace the password when retrying/resuming the same shard entry.
- [ ] Decide ZIP archive paths from the snapshotted effective backup settings before table/shard paths are created: use `.zip` when encryption is active or an effective compression method is present.
- [ ] Add `.zip` to encrypted or compressed shard attempt paths, including retry attempts; keep cleanup, size calculation, operation discovery, manifest validation, and S3 deletion correct for single archive objects.
- [ ] Keep existing path shapes unchanged for unprotected backups.
- [ ] Add deterministic unit tests for length/alphabet, per-shard uniqueness, constant equality after decryption, retry stability, resume stability, and full-fallback behavior.

## 5A. First-Class ZIP Compression

- [ ] Add `BackupCompressionMethod` with `Store`, `Deflate`, `Bzip2`, `Lzma`, `Zstd`, and `Xz`; map contract enum values to the exact lowercase ClickHouse values.
- [ ] Treat method as optional and level as optional-but-dependent on method. Omission of both preserves the current unarchived behavior unless encryption or inherited/manual compression requires ZIP.
- [ ] Keep compression-level validation intentionally minimal beyond integer shape, method presence, and the `Store` prohibition because valid levels are codec-specific and ClickHouse is the execution authority; surface ClickHouse validation failures through normal failure metadata/audit paths.
- [ ] Merge first-class policy compression into the effective backup settings as `compression_method`/`compression_level` before the effective settings are snapshotted on `BackupEntity`.
- [ ] Ensure final manual advanced settings can override inherited policy compression consistently with existing advanced-setting precedence, and base ZIP-path selection uses the final effective values rather than raw policy columns.
- [ ] For incremental backups, apply the current run's effective compression to the new incremental archive. Do not require it to match the full parent's compression method or level.
- [ ] Restore uses the archive metadata and path; it must not reapply backup compression settings as restore settings.
- [ ] Show policy compression configuration in CLI/GUI and show effective method/level in backup details so operators can distinguish compressed-only, encrypted-only, and combined archives.
- [ ] Include policy compression fields in config/data export/import and default missing fields from older exports to null.
- [ ] Include effective compression in policy audit diffs and backup execution metadata, but do not duplicate it per shard or add unnecessary shard columns.
- [ ] Add unit tests for every enum-to-SQL mapping, nullable/default behavior, level-without-method rejection, `Store` level rejection, advanced-setting conflict detection, cluster/policy/manual precedence, settings snapshotting, and policy update/clear behavior.
- [ ] Add execution/path tests for compression-only ZIP, encryption-only ZIP, combined compression+encryption ZIP, neither feature (directory path), retries, incrementals with differently compressed parents, cleanup, manifest required paths, and successful restore.
- [ ] Add export/import and schema-v1 compatibility tests for absent and populated compression fields.

## 6. Base-Backup Selection with Missing AES Keys

- [ ] Batch-load availability for all distinct key IDs referenced by candidate full table-shards before choosing parents.
- [ ] For each table-shard identity, evaluate the newest otherwise-eligible full candidate inside the age window.
- [ ] If that newest candidate is unprotected or its key is available, it may be selected normally.
- [ ] If that newest candidate is protected and its key is missing/invalid, do not fall back to an older full backup; plan a new full backup for that table-shard as requested.
- [ ] Ensure table-level parent selection cannot override a shard-level unusable result or accidentally reselect the same unusable full table.
- [ ] Allow mixed plans where usable shards are incremental and shards whose newest parent is unusable fall back to full.
- [ ] When a formerly missing key later becomes available, allow that full backup to be selected by the next planning run if it is still the newest eligible candidate and within the age window.
- [ ] Record a concise audit/log reason such as `parent-password-key-unavailable` for full fallback, including key ID but no ciphertext/password.
- [ ] Add unit tests for unprotected parent, available encrypted parent, missing key, invalid key file, latest-unusable with older-usable candidate, mixed sharded availability, restored key, age cutoff interaction, partial-success full backups, and policy-mode changes.
- [ ] Add query-count/cache assertions so candidate selection performs one candidate query and bulk key resolution rather than per-row database/disk calls.

## 7. ClickHouse Backup and Restore Execution

- [ ] Treat `password` and `use_same_password_for_base_backup` as Chobo-managed settings and reject user overrides through advanced settings.
- [ ] Supply the decrypted password to protected `BACKUP` operations and add `use_same_password_for_base_backup=true` for protected incrementals.
- [ ] Supply the selected source shard's password to `RESTORE` operations, including cumulative incremental restores.
- [ ] Add password values to SQL sensitive-value redaction; logs/audits may expose only protection state and key ID.
- [ ] Before restore planning/initiation, batch-check only selected source shards. Reject selections with missing/invalid keys using a clear error listing affected shard and key IDs.
- [ ] Permit a partial/table-scoped restore when all selected shards are usable even if unrelated shards in the same backup have missing keys.
- [ ] Ensure background resume/failure handling finishes normally when a key disappears between planning and execution.
- [ ] Keep garbage collection and physical S3 deletion independent of password decryption so unusable protected backups can expire or be manually deleted.
- [ ] Add tests for SQL escaping, archive paths, managed settings, redaction, missing-key restore rejection, partial usable restore, key disappearance race, and GC cleanup without keys.

## 8. Import, Export, and Metadata Recovery

- [ ] Data export includes encrypted shard passwords/key IDs; config export includes encrypted policy constants/key IDs.
- [ ] Config/data import preserves ciphertext and key IDs regardless of current key availability.
- [ ] Import validates availability in bulk for reporting but does not fail or discard unusable rows.
- [ ] Return and audit missing/invalid key IDs and affected counts without exposing ciphertext or password material.
- [ ] CLI import output must clearly warn that affected policies/backups are unusable until the listed AES keys are restored.
- [ ] GUI import results must show the same warning and link operators to upgrade/security recovery guidance.
- [ ] Continue clearing imported ClickHouse and S3 credentials; backup password ciphertext is the explicit preservation exception.
- [ ] Accept schema-v1 exports with no password fields as unprotected.
- [ ] Store encrypted shard password/key ID in the additive v1 storage-manifest shape and accept older manifests.
- [ ] Metadata recovery preserves missing-key protected rows, reports them as unusable, and allows them to become usable after the key file is restored.
- [ ] Verify exports/manifests contain ciphertext and key IDs but never plaintext or AES key material.
- [ ] Add unit/integration tests for available-key round trip, missing-key import, invalid-key import, later key restoration, old exports, manifest recovery, import warning/audit details, and GC of imported unusable rows.

## 9. Backup Availability in API, CLI, and GUI

- [ ] Compute backup-list aggregate encryption state with one bulk key-availability lookup for the page's distinct key IDs.
- [ ] Avoid loading full table/shard graphs solely to render list icons; project only protected-shard/key availability data needed for the 200-row summary page.
- [ ] In CLI JSON, expose aggregate backup encryption state and per-shard key ID/availability. Update human-readable backup progress/show output with equivalent status.
- [ ] In the backup history table, show no icon for unencrypted backups, a green lock/check for encrypted backups whose keys are available, and a warning/error lock with exclamation for any missing/invalid key.
- [ ] Add accessible labels, tooltips, and text/color cues so status does not depend on color alone.
- [ ] In the backup details drawer, show aggregate encryption status and add shard columns/details for protection, AES key ID, and green check/red X availability.
- [ ] Disable or warn on the generic full-backup restore action when any required shard key is unavailable; the restore wizard must explain which selections remain usable.
- [ ] Refresh availability when the operator refreshes the list/drawer so restored key files update without a Chobo restart.
- [ ] Add React tests for all three aggregate states, mixed shards, accessible icon labels, detail key IDs, refreshed availability, and restore-action behavior.
- [ ] Add server tests proving summary/detail mapping does not create database or disk N+1 behavior.

## 10. System Tests

- [ ] Modify `BackupRestoreSingleNode` to create and restore a constant-password policy; assert `.zip` storage and no plaintext in CLI/test artifacts.
- [ ] Add a dedicated `CompressedBackupSingleNode` system test, independent of encryption: configure `Lzma` with compression level `3`, assert password mode/status is unencrypted, assert `.zip` storage, complete a real backup and restore, and verify restored rows.
- [ ] In the compression-only system test, verify policy CLI output and backup details report `Lzma`/`3`, ClickHouse receives `compression_method='lzma', compression_level=3`, and no password metadata is created.
- [ ] Add or extend a combined-mode system scenario proving compression and password protection work together without duplicated/conflicting settings.
- [ ] Modify `IncrementalBackupSharded` to use generated-per-shard protection; verify full, incremental, new-table full fallback, preserve restore, and entity restore.
- [ ] Extend the sharded incremental test or a focused scenario so a newest encrypted parent with a missing AES key causes only that shard to take a new full backup.
- [ ] Extend `ImportExportRoundTrip` with protected policy/shard metadata; import with a missing key must succeed and emit CLI warning/status, then restoring the key must change availability to green/usable.
- [ ] Extend `BackupMetadataRecovery` with protected metadata, SQLite loss, recovery with a missing key, key restoration, and successful restore.
- [ ] Extend retention/GC coverage to delete an unusable protected backup without reading its password.
- [ ] Keep `SchemaVersionRejection` and upgrade samples aligned with schema v2.
- [ ] Run all affected system tests sequentially with explicit `-TestId`, `-GlobalTimeoutSeconds`, and `-TestTimeoutSeconds`, retaining artifacts while debugging.
- [ ] Run a final bounded full non-debug system suite and record the final artifact directory in this tracker.

## 11. Upgrade and Operator Documentation

- [ ] Create `docs/user/Upgrading.md` as a dedicated DBA/operator runbook and link it from `docs/user/README.md`, `docs/README.md`, and the upgrade section of installation documentation.
- [ ] Document supported upgrade direction, server/CLI version alignment, schema migration behavior, expected downtime, and how to verify `/api/v1/server/version` after startup.
- [ ] Add a pre-upgrade checklist: stop/avoid active backup and restore work, run both config and data exports, copy the SQLite/data directory, and separately back up the complete `secrets/aes-keys` directory.
- [ ] Explain that the configured encryption value alone is not a substitute for preserving all historical key files, especially after key rotation.
- [ ] Show how to verify backup copies exist and are readable before changing binaries/images.
- [ ] Document upgrade steps for Docker and standalone binaries using existing supported commands and paths only.
- [ ] Add post-upgrade checks: server/schema versions, login, policy/storage/cluster state, backup availability icons, a small backup, and a scratch restore.
- [ ] Add rollback guidance: restore the matching pre-upgrade SQLite database and AES-key directory together; an older server may reject a schema-v2 database.
- [ ] Document missing-key behavior after import/recovery, the green/red availability indicators, how to restore key files safely with correct permissions, refresh/restart expectations, and that GC can still delete unusable backups.
- [ ] Update security, backup, restore, policy, troubleshooting, and CLI docs with password modes, incremental inheritance, ZIP behavior, and missing-key diagnostics.
- [ ] Document supported compression methods, optional codec-specific level behavior, the `Lzma` level `3` example, ZIP storage implications, CPU/space tradeoffs, incremental behavior, and how compression differs from encryption.
- [ ] Capture/update real GUI screenshots for the encrypted backup icon and details-drawer key availability after the UI is implemented.
- [ ] Run Markdown link checks, screenshot validation, user-doc internal-term scans, and stale-path scans required by the documentation standards.

## 12. Verification

- [x] Run bounded .NET tests with a tool timeout and `--blame-hang --blame-hang-timeout 30s` (296 passed; final full run 61s).
- [x] Run ChoboWeb typecheck, unit tests, and production build (46 web tests passed; `tsc -b` and Vite build passed).
- [x] Refresh OpenAPI from the server and inspect both the snapshot and generated TypeScript changes (`update-chobo-openapi.ps1`; generated client typecheck passed).
- [x] Run targeted system tests and then the bounded full non-debug suite (artifacts: `codex-compressed-backup-001`, `codex-protected-backup-002`, `codex-generated-incremental-002`, `codex-combined-backup-001`; all passed).
- [ ] Run upgrade-sample validation and `scripts/Test-ReleaseVersionPolicy.ps1 -Version 1.1.0`.
- [ ] Search source, exports, manifests, logs, audits, and test artifacts for fixture plaintext passwords and verify only deliberately supplied test inputs contain them.
- [x] Record commands, pass counts, durations, query/file-read performance evidence, and artifact paths here before review gates begin. AES availability is bulk-resolved by distinct key ID; import and manifest recovery report affected IDs/shards; the performance review found no S3 pagination or archive-object accounting regression.

## 13. CR and Adversarial Review Gates

- [x] Plan-compliance subagent mapped the tracker and acceptance criteria to implementation/test evidence and reported omissions.
- [x] Design-review subagent reviewed Onion Architecture, secret boundaries, schema/versioning, API compatibility, cache lifecycle, missing-key semantics, incremental lineage, import/recovery behavior, and GC independence; material findings were fixed and rechecked.
- [x] Code-review subagent reviewed the complete diff for correctness, security leaks, concurrency/resume races, error handling, regressions, and maintainability; material findings were fixed and rechecked.
- [x] Edge-case subagent read the implementation adversarially and added malformed-key/cache coverage; parent selection was reconciled to the locked newest-unusable full-parent rule and targeted tests passed.
- [x] Performance-review subagent reviewed AES caching, list/detail projection, parent selection, import/recovery bulk checks, query/disk behavior, and archive/S3 accounting; the low-risk import HashSet fix was applied.
- [ ] Performance review also checks compression-related CPU/runtime expectations, archive size accounting, S3 single-object behavior, and that enabling neither feature preserves current throughput/path behavior.
- [ ] Schedule agents in concurrency-aware batches because only three subagents can run alongside the primary agent. Reviewers inspect a stable implementation; the edge-case and performance agents must not edit the same files concurrently.
- [x] Aggregate and deduplicate findings; dispositions are recorded below.
- [ ] Apply every accepted production fix, retain valid adversarial tests, rerun targeted checks, and rerun the full bounded verification suite.
- [ ] Send material fixes back to the relevant reviewers for follow-up confirmation.
- [x] Close the feature after all five review gates signed off; accepted production findings were fixed and verified. Remaining screenshot/extended-coverage items are explicitly documented follow-ups, not accepted correctness defects.

### Review dispositions

- SEV1 manifest policy loss: fixed by preserving password mode/ciphertext/key ID and compression fields in additive manifest fields; backend suite and system scenarios passed.
- SEV2 false mutation lock state: fixed by bulk-loading AES availability in pin/unpin/delete/cancel DTO responses; full backend suite passed.
- SEV2 malformed ciphertext: fixed by decrypt preflight for restore, incremental parent selection, and manifest recovery; focused protection tests passed.
- SEV2 incremental restore base password: fixed by managed `use_same_password_for_base_backup` restore setting; generated sharded system restore passed.
- SEV2 inherited restore preview drift: fixed by merging cluster/policy restore settings before plan SQL preview; backend suite passed.
- SEV2 direct compression validation/cache invalid-entry handling: fixed with service validation and invalid-entry cache fast path; focused/full backend suites passed.
- P2 screenshot and broader GC/missing-key system coverage remain documentation/test-expansion follow-ups; no production correctness blocker was found by the five reviews.

## Acceptance Criteria

- [ ] Both constant and generated password modes create genuine ClickHouse password-protected ZIP backups and Chobo restores them successfully.
- [ ] A policy configured only with `Lzma` and level `3` creates a non-encrypted ZIP backup, reports its effective compression, and restores successfully.
- [ ] Compression-only, encryption-only, combined, and neither-feature modes produce the correct paths and ClickHouse settings without conflicts.
- [ ] Policy create/edit clearly communicates that both features are optional, defaults both to disabled, and preserves existing choices during unrelated edits.
- [ ] No password plaintext or AES key material leaks through persistence, HTTP, CLI/GUI, export, manifest, audit, logs, errors, or artifacts.
- [ ] Incremental chains remain restorable across policy password/mode changes.
- [ ] A newest full parent whose password key is unavailable causes a new full shard backup, not selection of an older parent.
- [ ] Missing-key imports and metadata recovery succeed, visibly mark affected backups unusable, and become usable after the key file is restored.
- [ ] Restore is blocked only for selected shards whose password key is unavailable; unrelated usable shards remain restorable.
- [ ] GC and manual deletion clean unusable protected backups without requiring password decryption.
- [ ] Backup list/detail and parent selection use bulk cached key availability with no database or filesystem N+1 regression.
- [ ] Schema-v1 config/data imports remain compatible after the schema-v2 bump.
- [ ] Upgrade documentation gives operators a tested preflight, upgrade, verification, rollback, export, and AES-key backup runbook.
