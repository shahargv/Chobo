# ClickHouse advanced backup/restore settings

Implemented cluster, policy, and per-operation ClickHouse BACKUP/RESTORE advanced settings.

Precedence:
- Backup: cluster backup settings -> policy backup settings -> manual run final dictionary.
- Restore: target cluster restore settings -> current backup policy restore settings -> manual restore final dictionary.

Notes:
- SQLite columns store JSON object text and default to `{}` for compatibility with older config/data.
- Schema version remains unchanged per user instruction.
- Backup runs store the effective backup settings used for execution.
- Restore runs store the effective restore settings used for execution.
- Restore defaults are evaluated at restore initiation time; backup metadata does not freeze restore settings.
- Chobo-managed settings are rejected from user config: `base_backup` for backup, `allow_non_empty_tables` for restore.
- Web advanced settings editors are collapsed by default and use a reusable component across cluster, policy, manual backup, and restore launch flows.

Subagent review follow-up:
- Preserved existing cluster/policy advanced settings when update requests omit those fields; explicit `{}` still clears.
- Prevented manual backup/restore operation launch while inherited settings preview is loading or failed.
- Fixed the editor draft-row behavior so new blank rows remain visible and validation can block operation launch.
- Fixed backup failure audit ordering so terminal `failed` audit is written before best-effort failed-manifest writes to unreachable S3.

Verification performed:
- `npm run typecheck` in `ChoboWeb`
- `npm test` in `ChoboWeb`: 8 files, 29 tests passed
- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s`: 194 passed
- `TestingSuite\TestManager.ps1 -TestId codex-clickhouse-settings-failure-rerun3-20260627-1805 -TestName FailureScenario -GlobalTimeoutSeconds 1000 -TestTimeoutSeconds 520`: passed
- `TestingSuite\TestManager.ps1 -TestId codex-clickhouse-settings-full-20260627-1808 -RunAllConcurrency 3 -GlobalTimeoutSeconds 3600 -TestTimeoutSeconds 520`: 20/20 passed

System test coverage:
- Extended `TestingSuite/Tests/ChoboCrudSmoke/TestDefinition.psd1` to validate cluster and policy advanced backup/restore settings through CLI/API JSON output.
- Extended `TestingSuite/Tests/FailureScenario/TestDefinition.psd1` audit wait and fixed the product audit ordering issue it exposed.
