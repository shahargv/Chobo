# Backup Metadata Recovery State

Support durable storage-side backup manifests so Chobo can rebuild SQLite backup metadata after local DB loss.

## Implementation State
- [x] Added versioned manifest and recovery contracts.
- [x] Added S3/listing support through `IBackupStorageOperations`.
- [x] Added `IBackupStorageManifestService` and implementation.
- [x] Backup runner writes manifests during and after backup execution.
- [x] Recovery APIs and `ChoboCli backups recover` are wired.
- [x] `clusters update-credentials` API and CLI are wired.
- [x] Missing initialized SQLite startup creates a fresh DB and logs a warning.
- [x] Test hook can delete SQLite and crash the server.
- [x] `BackupMetadataRecovery` system test scenario added.
- [x] Full Docker system scenario validated.

## Test State
- [x] Unit coverage for manifest write and credential redaction.
- [x] Unit coverage for recovery importing failed backup metadata and idempotent re-run.
- [x] Unit coverage for credential-only cluster update.
- [x] Unit coverage for missing DB fresh bootstrap.
- [x] `BackupMetadataRecovery` system test added.
- [x] `BackupMetadataRecovery` system test passing.

## Verified
- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s`
- `.\TestingSuite\TestManager.ps1 -TestId codex-backup-metadata-recovery-final -TestName BackupMetadataRecovery -GlobalTimeoutSeconds 420 -TestTimeoutSeconds 300 -CleanTestResults`
- `dotnet build Chobo.sln -v minimal -m:1 --no-restore /p:UseAppHost=false -clp:ErrorsOnly`
- `.\TestingSuite\TestManager.ps1 -TestId codex-backup-metadata-recovery-warning-fix -TestName BackupMetadataRecovery -GlobalTimeoutSeconds 420 -TestTimeoutSeconds 300 -CleanTestResults`
