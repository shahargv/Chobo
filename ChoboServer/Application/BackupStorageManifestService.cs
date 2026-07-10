using System.Text;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public interface IBackupStorageManifestService
{
    Task WriteManifestAsync(Guid backupId, CancellationToken cancellationToken = default);
    Task<BackupMetadataRecoveryResult> RecoverFromPathAsync(RecoverBackupMetadataFromPathRequest request, CancellationToken cancellationToken = default);
    Task<BackupMetadataRecoveryResult> RecoverFromScanAsync(RecoverBackupMetadataScanRequest request, CancellationToken cancellationToken = default);
}

public sealed class BackupStorageManifestService(
    ChoboDbContext db,
    IBackupStorageOperations storage,
    IClickHouseClusterMetadataService metadata,
    IAesKeyRepository aesKeys,
    ICredentialProtector protector,
    IAuditService audit,
    Serilog.ILogger logger) : IBackupStorageManifestService
{
    public const int ManifestVersion = 1;
    public const string ManifestDirectoryName = "_chobo";
    public const string ManifestFileExtension = ".json";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupStorageManifestService>();

    public async Task WriteManifestAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var backup = await LoadBackupAsync(backupId, cancellationToken)
            ?? throw new InvalidOperationException($"Backup {backupId} was not found.");
        if (backup.Target is null)
        {
            throw new InvalidOperationException("Backup target was not loaded.");
        }

        var manifest = ToManifest(backup);
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions));
        var path = ManifestPath(backup);
        await storage.WriteObjectAsync(backup.Target, path, content, cancellationToken);

        _logger.Information("Wrote backup storage manifest for backup {BackupId} to {StoragePath}.", backup.Id, path);
    }

    public async Task<BackupMetadataRecoveryResult> RecoverFromPathAsync(RecoverBackupMetadataFromPathRequest request, CancellationToken cancellationToken = default)
    {
        var target = await FindTargetAsync(request.TargetId, cancellationToken);
        var path = NormalizeManifestPath(request.BackupPath);
        return await RecoverManifestsAsync(target, [(path, await ReadManifestAsync(target, path, cancellationToken))], cancellationToken);
    }

    public async Task<BackupMetadataRecoveryResult> RecoverFromScanAsync(RecoverBackupMetadataScanRequest request, CancellationToken cancellationToken = default)
    {
        var target = await FindTargetAsync(request.TargetId, cancellationToken);
        var objectPaths = await storage.ListObjectPathsAsync(target, request.ScanRoot ?? "", cancellationToken);
        var manifestPaths = objectPaths
            .Where(IsScannableManifestPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var candidates = new List<(string Source, BackupStorageManifestV1? Manifest)>();
        var errors = new List<string>();
        foreach (var path in manifestPaths)
        {
            try
            {
                candidates.Add((path, await ReadManifestAsync(target, path, cancellationToken)));
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
                candidates.Add((path, null));
            }
        }

        var result = await RecoverManifestsAsync(target, candidates, cancellationToken);
        return result with { Errors = result.Errors.Concat(errors).ToList(), SkippedManifestCount = result.SkippedManifestCount + errors.Count };
    }

    private async Task<BackupMetadataRecoveryResult> RecoverManifestsAsync(
        BackupTargetEntity scanTarget,
        IReadOnlyList<(string Source, BackupStorageManifestV1? Manifest)> candidates,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var items = new List<BackupMetadataRecoveryItem>();
        var unique = new Dictionary<Guid, (string Source, BackupStorageManifestV1 Manifest)>();
        var skipped = 0;

        foreach (var (source, manifest) in candidates)
        {
            if (manifest is null)
            {
                skipped++;
                continue;
            }
            if (manifest.ManifestVersion != ManifestVersion)
            {
                errors.Add($"{source}: unsupported manifest version {manifest.ManifestVersion}.");
                skipped++;
                continue;
            }
            if (unique.ContainsKey(manifest.Backup.Id))
            {
                skipped++;
                continue;
            }

            unique.Add(manifest.Backup.Id, (source, manifest));
        }

        var orderedManifests = unique.Values
            .OrderBy(x => x.Manifest.Backup.CreatedAt)
            .ThenBy(x => x.Manifest.Backup.BackupType == BackupType.Full ? 0 : 1)
            .ThenBy(x => x.Manifest.Backup.Id)
            .ToList();
        var protectedManifestShards = orderedManifests
            .SelectMany(x => x.Manifest.Tables)
            .SelectMany(x => x.Shards)
            .Where(x => x.EncryptedBackupPassword is not null)
            .ToList();
        var keyAvailability = await aesKeys.GetAvailabilitiesAsync(
            protectedManifestShards.Select(x => x.EncryptedBackupPasswordKeyId).OfType<Guid>(),
            cancellationToken);
        var invalidCipherKeyIds = new HashSet<Guid>();
        foreach (var shard in protectedManifestShards.Where(x => x.EncryptedBackupPasswordKeyId is { } keyId && keyAvailability.GetValueOrDefault(keyId) == AesKeyAvailability.Available))
        {
            try
            {
                var password = await protector.DecryptAsync(shard.EncryptedBackupPassword, shard.EncryptedBackupPasswordKeyId, cancellationToken);
                if (string.IsNullOrEmpty(password))
                {
                    invalidCipherKeyIds.Add(shard.EncryptedBackupPasswordKeyId!.Value);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or CryptographicException)
            {
                invalidCipherKeyIds.Add(shard.EncryptedBackupPasswordKeyId!.Value);
            }
        }
        var missingOrInvalidKeyIds = protectedManifestShards
            .Select(x => x.EncryptedBackupPasswordKeyId)
            .OfType<Guid>()
            .Where(keyId => keyAvailability.GetValueOrDefault(keyId) != AesKeyAvailability.Available)
            .Concat(invalidCipherKeyIds)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        var affectedBackupShardCount = protectedManifestShards.Count(x =>
            x.EncryptedBackupPasswordKeyId is not { } keyId ||
            keyAvailability.GetValueOrDefault(keyId) != AesKeyAvailability.Available ||
            invalidCipherKeyIds.Contains(keyId));
        var keyWarning = affectedBackupShardCount == 0
            ? null
            : $"Recovered {affectedBackupShardCount} protected backup shard(s) with missing or invalid AES keys. Restore the listed key files before using those backups.";
        var validations = new Dictionary<Guid, RecoveryStorageValidation>();
        foreach (var (source, manifest) in orderedManifests)
        {
            var validation = await ValidateRequiredStoragePathsAsync(scanTarget, manifest, cancellationToken);
            validations[manifest.Backup.Id] = validation;
            foreach (var missingPath in validation.MissingPaths)
            {
                errors.Add($"{source}: required storage path is missing or empty: {missingPath}");
            }
        }

        var imported = 0;
        var updated = 0;
        var importScope = new RecoveryImportScope();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var (source, manifest) in orderedManifests)
            {
                var validation = validations[manifest.Backup.Id];
                var existed = await db.Backups.AnyAsync(x => x.Id == manifest.Backup.Id, cancellationToken);
                await ImportManifestAsync(scanTarget, manifest, validation, importScope, cancellationToken);
                if (existed)
                {
                    updated++;
                }
                else
                {
                    imported++;
                }

                var degraded = validation.MissingPaths.Count > 0;
                items.Add(new BackupMetadataRecoveryItem(
                    manifest.Backup.Id,
                    degraded ? BackupRunStatus.PartiallySucceeded : manifest.Backup.Status,
                    source,
                    !existed,
                    existed,
                    degraded
                        ? $"Imported backup metadata as PartiallySucceeded because {validation.MissingPaths.Count} required storage path(s) were missing."
                        : existed ? "Updated existing backup metadata." : "Imported backup metadata."));
            }

            await transaction.CommitAsync(cancellationToken);
            foreach (var clusterId in importScope.ClusterIds)
            {
                metadata.Invalidate(clusterId);
            }
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        await audit.RecordAsync("backup-metadata-recovered", AuditEntityType.Backup, null, new
        {
            scanTargetId = scanTarget.Id,
            scannedManifestCount = candidates.Count,
            importedBackupCount = imported,
            updatedBackupCount = updated,
            skippedManifestCount = skipped,
            missingOrInvalidAesKeyIds = missingOrInvalidKeyIds,
            affectedBackupShardCount,
            errors
        });

        return new BackupMetadataRecoveryResult(candidates.Count, imported, updated, skipped, items, errors, missingOrInvalidKeyIds, affectedBackupShardCount, keyWarning);
    }

    private async Task<RecoveryStorageValidation> ValidateRequiredStoragePathsAsync(BackupTargetEntity scanTarget, BackupStorageManifestV1 manifest, CancellationToken cancellationToken)
    {
        var requiredPaths = RequiredStoragePaths(manifest).ToList();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var path in requiredPaths)
        {
            var objects = await storage.ListObjectsAsync(scanTarget, path, cancellationToken);
            if (objects.Count == 0)
            {
                missing.Add(path);
            }
            else
            {
                present.Add(path);
            }
        }

        return new RecoveryStorageValidation(present, missing);
    }

    private async Task ImportManifestAsync(BackupTargetEntity scanTarget, BackupStorageManifestV1 manifest, RecoveryStorageValidation validation, RecoveryImportScope importScope, CancellationToken cancellationToken)
    {
        var target = await db.BackupTargets.FirstOrDefaultAsync(x => x.Id == manifest.Target.Id, cancellationToken);
        if (target is null)
        {
            target = new BackupTargetEntity { Id = manifest.Target.Id };
            db.BackupTargets.Add(target);
        }
        if (importScope.TargetIds.Add(manifest.Target.Id) || string.IsNullOrWhiteSpace(target.Name))
        {
            var manifestTargetType = ManifestTargetType(manifest.Target);
            if (!string.Equals(scanTarget.Type, manifestTargetType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Cannot recover metadata for target type '{manifestTargetType}' using scan target type '{scanTarget.Type}'.");
            }

            target.Name = manifest.Target.Name;
            target.Type = manifestTargetType;
            target.SettingsJson = JsonSerializer.Serialize(ManifestTargetSettings(manifest.Target), JsonOptions);
            target.SecretsJson = scanTarget.SecretsJson;
            target.IsDeleted = manifest.Target.IsDeleted;
            target.CreatedAt = manifest.Target.CreatedAt;
            target.UpdatedAt = manifest.Target.UpdatedAt;
            target.DeletedAt = manifest.Target.DeletedAt;
        }

        var cluster = await db.ClickHouseClusters.Include(x => x.AccessNodes).FirstOrDefaultAsync(x => x.Id == manifest.SourceCluster.Id, cancellationToken);
        if (cluster is null)
        {
            cluster = new ClickHouseClusterEntity { Id = manifest.SourceCluster.Id };
            db.ClickHouseClusters.Add(cluster);
        }
        if (importScope.ClusterIds.Add(manifest.SourceCluster.Id) || string.IsNullOrWhiteSpace(cluster.Name))
        {
            cluster.Name = manifest.SourceCluster.Name;
            cluster.Mode = manifest.SourceCluster.Mode;
            cluster.BackupRestoreMaxDop = manifest.SourceCluster.BackupRestoreMaxDop ?? 3;
            cluster.NodeMaxDopDefault = manifest.SourceCluster.NodeMaxDopDefault;
            cluster.NodeMaxDopOverridesJson = JsonSerializer.Serialize(manifest.SourceCluster.NodeMaxDopOverrides ?? [], JsonOptions);
            cluster.ShardMaxDopDefault = manifest.SourceCluster.ShardMaxDopDefault;
            cluster.ShardMaxDopOverridesJson = JsonSerializer.Serialize(manifest.SourceCluster.ShardMaxDopOverrides ?? [], JsonOptions);
            cluster.ClickHouseClusterName = manifest.SourceCluster.ClickHouseClusterName;
            cluster.IsDeleted = manifest.SourceCluster.IsDeleted;
            cluster.CreatedAt = manifest.SourceCluster.CreatedAt;
            cluster.UpdatedAt = manifest.SourceCluster.UpdatedAt;
            cluster.DeletedAt = manifest.SourceCluster.DeletedAt;
            db.ClickHouseAccessNodes.RemoveRange(cluster.AccessNodes);
            cluster.AccessNodes = manifest.SourceCluster.AccessNodes.Select(x => new ClickHouseAccessNodeEntity
            {
                Id = x.Id,
                ClusterId = cluster.Id,
                Host = x.Host,
                Port = x.Port,
                UseTls = x.UseTls
            }).ToList();
        }

        if (manifest.Policy is { } policyManifest)
        {
            var policy = await db.BackupPolicies.FirstOrDefaultAsync(x => x.Id == policyManifest.Id, cancellationToken);
            if (policy is null)
            {
                policy = new BackupPolicyEntity { Id = policyManifest.Id };
                db.BackupPolicies.Add(policy);
            }
            if (importScope.PolicyIds.Add(policyManifest.Id) || string.IsNullOrWhiteSpace(policy.Name))
            {
                policy.Name = policyManifest.Name;
                policy.SourceClusterId = policyManifest.SourceClusterId;
                policy.TargetId = policyManifest.TargetId;
                policy.SelectorJsonVersion = policyManifest.SelectorJsonVersion;
                policy.SelectorJson = JsonSerializer.Serialize(policyManifest.Selector, JsonOptions);
                policy.FullRetentionMinutes = policyManifest.Retention?.FullRetentionMinutes;
                policy.IncrementalRetentionMinutes = policyManifest.Retention?.IncrementalRetentionMinutes;
                policy.MinBackupsToKeep = policyManifest.Retention?.MinBackupsToKeep ?? 0;
                policy.MinFullBackupsToKeep = policyManifest.Retention?.MinFullBackupsToKeep ?? 0;
                policy.FailedBackupRetentionMode = policyManifest.FailedBackupRetentionMode;
                policy.MaxAgeHoursForBaseBackup = policyManifest.MaxAgeHoursForBaseBackup;
                policy.PasswordMode = policyManifest.PasswordMode;
                policy.EncryptedBackupPassword = policyManifest.EncryptedBackupPassword;
                policy.EncryptedBackupPasswordKeyId = policyManifest.EncryptedBackupPasswordKeyId;
                policy.CompressionMethod = policyManifest.CompressionMethod;
                policy.CompressionLevel = policyManifest.CompressionLevel;
                policy.IsDeleted = policyManifest.IsDeleted;
                policy.CreatedAt = policyManifest.CreatedAt;
                policy.UpdatedAt = policyManifest.UpdatedAt;
                policy.DeletedAt = policyManifest.DeletedAt;
            }
        }

        if (manifest.Schedule is { } scheduleManifest)
        {
            var schedule = await db.BackupSchedules.FirstOrDefaultAsync(x => x.Id == scheduleManifest.Id, cancellationToken);
            if (schedule is null)
            {
                schedule = new BackupScheduleEntity { Id = scheduleManifest.Id };
                db.BackupSchedules.Add(schedule);
            }
            if (importScope.ScheduleIds.Add(scheduleManifest.Id) || string.IsNullOrWhiteSpace(schedule.Name))
            {
                schedule.Name = scheduleManifest.Name;
                schedule.PolicyId = scheduleManifest.PolicyId;
                schedule.BackupType = scheduleManifest.BackupType;
                schedule.CronExpression = scheduleManifest.CronExpression;
                schedule.TimeZoneId = scheduleManifest.TimeZoneId;
                schedule.IsEnabled = scheduleManifest.IsEnabled;
                schedule.MissedRunGracePeriod = scheduleManifest.MissedRunGracePeriod;
                schedule.Description = scheduleManifest.Description;
                schedule.IsDeleted = scheduleManifest.IsDeleted;
                schedule.CreatedAt = scheduleManifest.CreatedAt;
                schedule.UpdatedAt = scheduleManifest.UpdatedAt;
                schedule.DeletedAt = scheduleManifest.DeletedAt;
            }
        }
        foreach (var schemaManifest in manifest.Schemas)
        {
            var schema = await db.SchemaDefinitions.FirstOrDefaultAsync(x => x.Id == schemaManifest.Id, cancellationToken)
                ?? await db.SchemaDefinitions.FirstOrDefaultAsync(x => x.SchemaHash == schemaManifest.SchemaHash, cancellationToken);
            if (schema is null)
            {
                schema = new SchemaDefinitionEntity { Id = schemaManifest.Id };
                db.SchemaDefinitions.Add(schema);
            }
            schema.SchemaHash = schemaManifest.SchemaHash;
            schema.Database = schemaManifest.Database;
            schema.Table = schemaManifest.Table;
            schema.Engine = schemaManifest.Engine;
            schema.CreateTableSql = schemaManifest.CreateTableSql;
            schema.ColumnsJson = schemaManifest.ColumnsJson;
            schema.CreatedAt = schemaManifest.CreatedAt;
        }

        var degraded = validation.MissingPaths.Count > 0;
        var recoveryError = degraded
            ? $"Recovered from storage manifest with missing storage data path(s): {string.Join(", ", validation.MissingPaths)}"
            : null;
        var backup = await db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).FirstOrDefaultAsync(x => x.Id == manifest.Backup.Id, cancellationToken);
        if (backup is null)
        {
            backup = new BackupEntity { Id = manifest.Backup.Id };
            db.Backups.Add(backup);
        }
        backup.TriggerType = manifest.Backup.TriggerType;
        backup.Status = degraded ? BackupRunStatus.PartiallySucceeded : manifest.Backup.Status;
        backup.BackupType = manifest.Backup.BackupType;
        backup.SourceClusterId = manifest.Backup.SourceClusterId;
        backup.StorageRootPath = manifest.Backup.StorageRootPath;
        backup.TargetId = manifest.Backup.TargetId;
        backup.PolicyId = manifest.Backup.PolicyId;
        backup.ScheduleId = manifest.Backup.ScheduleId;
        backup.RequestedByUserId = manifest.Backup.RequestedByUserId;
        backup.RequestedByName = manifest.Backup.RequestedByName;
        backup.ManualRequestJson = manifest.Backup.ManualRequestJson;
        backup.CreatedAt = manifest.Backup.CreatedAt;
        backup.QueuedAt = manifest.Backup.QueuedAt;
        backup.StartedAt = manifest.Backup.StartedAt;
        backup.CompletedAt = manifest.Backup.CompletedAt;
        backup.Error = recoveryError ?? manifest.Backup.Error;
        backup.FailureReason = recoveryError ?? manifest.Backup.FailureReason;
        backup.IsPinned = manifest.Backup.IsPinned;
        backup.PinnedAt = manifest.Backup.PinnedAt;
        backup.PinnedByUserId = manifest.Backup.PinnedByUserId;
        backup.PinnedByName = manifest.Backup.PinnedByName;
        backup.DeletionReason = manifest.Backup.DeletionReason;
        backup.DeletionRequestedAt = manifest.Backup.DeletionRequestedAt;
        backup.DeletionStartedAt = manifest.Backup.DeletionStartedAt;
        backup.DeletedAt = manifest.Backup.DeletedAt;
        backup.DeletionError = manifest.Backup.DeletionError;
        backup.DeletionAttemptCount = manifest.Backup.DeletionAttemptCount;

        foreach (var tableManifest in manifest.Tables)
        {
            var table = backup.Tables.FirstOrDefault(x => x.Id == tableManifest.Id);
            if (table is null)
            {
                table = new BackupTableEntity { Id = tableManifest.Id, BackupId = backup.Id };
                backup.Tables.Add(table);
            }
            table.EffectiveBackupType = tableManifest.EffectiveBackupType;
            table.ParentFullBackupId = tableManifest.ParentFullBackupId;
            table.ParentFullBackupTableId = tableManifest.ParentFullBackupTableId;
            table.Database = tableManifest.Database;
            table.Table = tableManifest.Table;
            table.Engine = tableManifest.Engine;
            table.DataBackedUp = tableManifest.DataBackedUp;
            table.SchemaDefinitionId = tableManifest.SchemaDefinitionId;
            var tableStoragePath = ManifestStoragePath(tableManifest);
            table.StoragePath = tableStoragePath;
            table.BackupSizeBytes = tableManifest.BackupSizeBytes;
            var tablePathMissing = tableManifest.DataBackedUp && tableManifest.Shards.Count == 0 && IsMissing(validation, tableStoragePath);
            table.Status = tablePathMissing ? BackupTableStatus.Failed : tableManifest.Status;
            table.ClickHouseOperationId = tableManifest.ClickHouseOperationId;
            table.ClickHouseStatus = tablePathMissing ? "RECOVERY_MISSING_STORAGE_PATH" : tableManifest.ClickHouseStatus;
            table.StartedAt = tableManifest.StartedAt;
            table.CompletedAt = tableManifest.CompletedAt;
            table.Error = tablePathMissing ? $"Required storage data path was missing during metadata recovery: {tableStoragePath}" : tableManifest.Error;

            foreach (var shardManifest in tableManifest.Shards)
            {
                var shard = table.Shards.FirstOrDefault(x => x.Id == shardManifest.Id);
                if (shard is null)
                {
                    shard = new BackupTableShardEntity { Id = shardManifest.Id, BackupTableId = table.Id };
                    table.Shards.Add(shard);
                }
                shard.EffectiveBackupType = shardManifest.EffectiveBackupType;
                shard.ParentFullBackupId = shardManifest.ParentFullBackupId;
                shard.ParentFullBackupTableShardId = shardManifest.ParentFullBackupTableShardId;
                shard.SourceShardNumber = shardManifest.SourceShardNumber;
                shard.SourceShardName = shardManifest.SourceShardName;
                shard.ReplicaNumber = shardManifest.ReplicaNumber;
                shard.Host = shardManifest.Host;
                shard.Port = shardManifest.Port;
                shard.UseTls = shardManifest.UseTls;
                var shardStoragePath = ManifestStoragePath(shardManifest);
                shard.StoragePath = shardStoragePath;
                shard.BackupSizeBytes = shardManifest.BackupSizeBytes;
                var shardPathMissing = IsMissing(validation, shardStoragePath);
                shard.Status = shardPathMissing ? BackupTableStatus.Failed : shardManifest.Status;
                shard.ClickHouseOperationId = shardManifest.ClickHouseOperationId;
                shard.ClickHouseStatus = shardPathMissing ? "RECOVERY_MISSING_STORAGE_PATH" : shardManifest.ClickHouseStatus;
                shard.StartedAt = shardManifest.StartedAt;
                shard.CompletedAt = shardManifest.CompletedAt;
                shard.Error = shardPathMissing ? $"Required storage data path was missing during metadata recovery: {shardStoragePath}" : shardManifest.Error;
                shard.EncryptedBackupPassword = shardManifest.EncryptedBackupPassword;
                shard.EncryptedBackupPasswordKeyId = shardManifest.EncryptedBackupPasswordKeyId;
            }

            if (tableManifest.Shards.Count > 0 && table.Shards.Any(x => x.Status == BackupTableStatus.Failed))
            {
                table.Status = AggregateRecoveredShardStatus(table.Shards.Select(x => x.Status));
                table.Error ??= "One or more shard storage data paths were missing during metadata recovery.";
                table.ClickHouseStatus = table.Status == BackupTableStatus.PartiallySucceeded
                    ? "RECOVERY_PARTIAL_MISSING_STORAGE_PATH"
                    : "RECOVERY_MISSING_STORAGE_PATH";
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<BackupStorageManifestV1> ReadManifestAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken)
    {
        var bytes = await storage.ReadObjectAsync(target, path, cancellationToken);
        return JsonSerializer.Deserialize<BackupStorageManifestV1>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("Backup metadata manifest is empty or invalid.");
    }

    private async Task<BackupTargetEntity> FindTargetAsync(Guid targetId, CancellationToken cancellationToken) =>
        await db.BackupTargets.FirstOrDefaultAsync(x => x.Id == targetId && !x.IsDeleted, cancellationToken)
        ?? throw new ArgumentException("Backup target was not found.");

    private Task<BackupEntity?> LoadBackupAsync(Guid backupId, CancellationToken cancellationToken) =>
        db.Backups
            .AsNoTracking()
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Target)
            .Include(x => x.Policy)
            .Include(x => x.Schedule)
            .Include(x => x.Tables).ThenInclude(x => x.SchemaDefinition)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);

    public static string ManifestPath(BackupEntity backup) =>
        JoinPath(ManifestRoot(backup.PolicyId), $"{backup.Id:N}{ManifestFileExtension}");

    private static string ManifestRoot(Guid? policyId) =>
        policyId is { } id
            ? $"backups/policy-{id:N}/{ManifestDirectoryName}"
            : $"backups/manual/{ManifestDirectoryName}";

    private static string NormalizeManifestPath(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup manifest path is required.");
        }
        var path = backupPath.Trim().TrimStart('/');
        if (!IsManifestObjectPath(path))
        {
            throw new ArgumentException("Backup path must point to a Chobo backup metadata manifest object such as backups/policy-<policy-id>/_chobo/<backup-id>.json.");
        }

        return path;
    }

    private static bool IsScannableManifestPath(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["backups", var owner, ManifestDirectoryName, var file]
            && (owner == "manual" || owner.StartsWith("policy-", StringComparison.Ordinal))
            && IsManifestFileName(file);
    }

    private static bool IsManifestObjectPath(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4
            && parts[^2] == ManifestDirectoryName
            && IsManifestFileName(parts[^1]);
    }

    private static bool IsManifestFileName(string fileName) =>
        fileName.EndsWith(ManifestFileExtension, StringComparison.Ordinal)
        && Guid.TryParse(fileName[..^ManifestFileExtension.Length], out _);

    private static string JoinPath(string first, string second) =>
        first.TrimEnd('/') + "/" + second.TrimStart('/');

    private static BackupStorageManifestV1 ToManifest(BackupEntity backup)
    {
        var schemas = backup.Tables
            .Select(x => x.SchemaDefinition)
            .Where(x => x is not null)
            .Cast<SchemaDefinitionEntity>()
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .Select(x => new BackupStorageManifestSchemaV1(x.Id, x.SchemaHash, x.Database, x.Table, x.Engine, x.CreateTableSql, x.ColumnsJson, x.CreatedAt))
            .OrderBy(x => x.Database)
            .ThenBy(x => x.Table)
            .ToList();

        var tables = backup.Tables.OrderBy(x => x.Database).ThenBy(x => x.Table).Select(ToManifestTable).ToList();

        return new BackupStorageManifestV1(
            ManifestVersion,
            ChoboApi.ApiVersion,
            ChoboApi.SchemaVersion,
            ChoboApi.ProductVersion,
            DateTimeOffset.UtcNow,
            new BackupStorageManifestRunV1(
                backup.Id,
                backup.TriggerType,
                backup.Status,
                backup.BackupType,
                backup.ContentMode,
                backup.SourceClusterId,
                backup.TargetId,
                backup.PolicyId,
                backup.ScheduleId,
                backup.RequestedByUserId,
                backup.RequestedByName,
                backup.ManualRequestJson,
                backup.StorageRootPath,
                backup.CreatedAt,
                backup.QueuedAt,
                backup.StartedAt,
                backup.CompletedAt,
                backup.Error,
                backup.FailureReason,
                backup.IsPinned,
                backup.PinnedAt,
                backup.PinnedByUserId,
                backup.PinnedByName,
                backup.DeletionReason,
                backup.DeletionRequestedAt,
                backup.DeletionStartedAt,
                backup.DeletedAt,
                backup.DeletionError,
                backup.DeletionAttemptCount),
            ToManifestTarget(backup.Target!),
            ToManifestCluster(backup.SourceCluster!),
            backup.Policy is null ? null : ToManifestPolicy(backup.Policy),
            backup.Schedule is null ? null : ToManifestSchedule(backup.Schedule),
            RequiredStoragePaths(tables).ToList(),
            schemas,
            tables);
    }

    private static BackupStorageManifestTargetV1 ToManifestTarget(BackupTargetEntity target) =>
        new(target.Id, target.Name, target.Type, ReadSettingsDictionary(target.SettingsJson), target.IsDeleted, target.CreatedAt, target.UpdatedAt, target.DeletedAt);


    private static string ManifestTargetType(BackupStorageManifestTargetV1 target) =>
        string.IsNullOrWhiteSpace(target.Type) || string.Equals(target.Type, "S3", StringComparison.OrdinalIgnoreCase) ? StorageProviderTypes.S3 : target.Type;

    private static IReadOnlyDictionary<string, JsonElement> ManifestTargetSettings(BackupStorageManifestTargetV1 target) =>
        target.Settings is not null
            ? CloneDictionary(target.Settings)
            : target.S3 is not null
                ? ReadSettingsDictionary(JsonSerializer.Serialize(target.S3, JsonOptions))
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, JsonElement> ReadSettingsDictionary(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, JsonElement> CloneDictionary(IReadOnlyDictionary<string, JsonElement> values) =>
        values.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
    private static BackupStorageManifestClusterV1 ToManifestCluster(ClickHouseClusterEntity cluster) =>
        new(cluster.Id, cluster.Name, cluster.Mode, cluster.AccessNodes.Select(x => new AccessNodeDto(x.Id, x.Host, x.Port, x.UseTls)).ToList(), cluster.BackupRestoreMaxDop, cluster.NodeMaxDopDefault, DeserializeNodeOverrides(cluster.NodeMaxDopOverridesJson), cluster.ShardMaxDopDefault, DeserializeShardOverrides(cluster.ShardMaxDopOverridesJson), cluster.ClickHouseClusterName, cluster.IsDeleted, cluster.CreatedAt, cluster.UpdatedAt, cluster.DeletedAt);

    private static BackupStorageManifestPolicyV1 ToManifestPolicy(BackupPolicyEntity policy) =>
        new(
            policy.Id,
            policy.Name,
            policy.SourceClusterId,
            policy.TargetId,
            policy.ContentMode,
            policy.SelectorJsonVersion,
            JsonSerializer.Deserialize<PolicySelector>(policy.SelectorJson, JsonOptions) ?? PolicySelector.Empty,
            policy.FullRetentionMinutes is null && policy.IncrementalRetentionMinutes is null
                ? null
                : new BackupRetentionDto(policy.FullRetentionMinutes, policy.IncrementalRetentionMinutes, policy.MinBackupsToKeep, policy.MinFullBackupsToKeep),
            policy.FailedBackupRetentionMode,
            policy.IsDeleted,
            policy.CreatedAt,
            policy.UpdatedAt,
            policy.DeletedAt,
            policy.MaxAgeHoursForBaseBackup,
            policy.PasswordMode,
            policy.EncryptedBackupPassword,
            policy.EncryptedBackupPasswordKeyId,
            policy.CompressionMethod,
            policy.CompressionLevel);

    private static BackupStorageManifestScheduleV1 ToManifestSchedule(BackupScheduleEntity schedule) =>
        new(schedule.Id, schedule.Name, schedule.PolicyId, schedule.BackupType, schedule.CronExpression, schedule.TimeZoneId, schedule.IsEnabled, schedule.MissedRunGracePeriod, schedule.Description, schedule.IsSystemDefault, schedule.IsDeleted, schedule.CreatedAt, schedule.UpdatedAt, schedule.DeletedAt);

    private static BackupStorageManifestTableV1 ToManifestTable(BackupTableEntity table) =>
        new(
            table.Id,
            table.BackupId,
            table.EffectiveBackupType,
            table.ParentFullBackupId,
            table.ParentFullBackupTableId,
            table.Database,
            table.Table,
            table.Engine,
            table.DataBackedUp,
            table.SchemaDefinitionId,
            table.StoragePath,
            table.BackupSizeBytes,
            table.Status,
            table.ClickHouseOperationId,
            table.ClickHouseStatus,
            table.StartedAt,
            table.CompletedAt,
            table.Error,
            table.Shards.OrderBy(x => x.SourceShardNumber).ThenBy(x => x.ReplicaNumber).Select(ToManifestShard).ToList());

    private static BackupStorageManifestShardV1 ToManifestShard(BackupTableShardEntity shard) =>
        new(
            shard.Id,
            shard.BackupTableId,
            shard.EffectiveBackupType,
            shard.ParentFullBackupId,
            shard.ParentFullBackupTableShardId,
            shard.SourceShardNumber,
            shard.SourceShardName,
            shard.ReplicaNumber,
            shard.Host,
            shard.Port,
            shard.UseTls,
            shard.StoragePath,
            shard.BackupSizeBytes,
            shard.Status,
            shard.ClickHouseOperationId,
            shard.ClickHouseStatus,
            shard.StartedAt,
            shard.CompletedAt,
            shard.Error)
        {
            EncryptedBackupPassword = shard.EncryptedBackupPassword,
            EncryptedBackupPasswordKeyId = shard.EncryptedBackupPasswordKeyId
        };


    private static string ManifestStoragePath(BackupStorageManifestTableV1 table) =>
        table.StoragePath ?? table.S3Path ?? throw new InvalidOperationException($"Backup manifest table '{table.Id}' is missing storagePath.");

    private static string ManifestStoragePath(BackupStorageManifestShardV1 shard) =>
        shard.StoragePath ?? shard.S3Path ?? throw new InvalidOperationException($"Backup manifest shard '{shard.Id}' is missing storagePath.");
    private static IEnumerable<string> RequiredStoragePaths(BackupStorageManifestV1 manifest)
    {
        if (manifest.RequiredStoragePaths is { Count: > 0 })
        {
            return manifest.RequiredStoragePaths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal);
        }

        return RequiredStoragePaths(manifest.Tables);
    }

    private static IEnumerable<string> RequiredStoragePaths(IReadOnlyList<BackupStorageManifestTableV1> tables) =>
        tables.SelectMany(table => table.DataBackedUp
                ? table.Shards.Count > 0
                    ? table.Shards.Select(ManifestStoragePath)
                    : [ManifestStoragePath(table)]
                : [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);


    private static BackupTableStatus AggregateRecoveredShardStatus(IEnumerable<BackupTableStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0)
        {
            return BackupTableStatus.Skipped;
        }
        if (list.All(x => x is BackupTableStatus.Succeeded or BackupTableStatus.Skipped))
        {
            return BackupTableStatus.Succeeded;
        }
        if (list.Any(x => x is BackupTableStatus.Succeeded or BackupTableStatus.PartiallySucceeded or BackupTableStatus.Skipped))
        {
            return BackupTableStatus.PartiallySucceeded;
        }

        return BackupTableStatus.Failed;
    }

    private static bool IsMissing(RecoveryStorageValidation validation, string path) =>
        !string.IsNullOrWhiteSpace(path) && validation.MissingPaths.Contains(path, StringComparer.Ordinal);

    private static IReadOnlyList<ClusterNodeMaxDopOverrideDto> DeserializeNodeOverrides(string json)
    {
        try { return JsonSerializer.Deserialize<List<ClusterNodeMaxDopOverrideDto>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<ClusterShardMaxDopOverrideDto> DeserializeShardOverrides(string json)
    {
        try { return JsonSerializer.Deserialize<List<ClusterShardMaxDopOverrideDto>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record RecoveryStorageValidation(IReadOnlySet<string> PresentPaths, IReadOnlyList<string> MissingPaths);

    private sealed class RecoveryImportScope
    {
        public HashSet<Guid> TargetIds { get; } = [];
        public HashSet<Guid> ClusterIds { get; } = [];
        public HashSet<Guid> PolicyIds { get; } = [];
        public HashSet<Guid> ScheduleIds { get; } = [];
    }
}
