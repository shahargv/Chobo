using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
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
    IAuditService audit,
    Serilog.ILogger logger) : IBackupStorageManifestService
{
    public const int ManifestVersion = 1;
    public const string ManifestRelativePath = "_chobo/backup-metadata.v1.json";
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
        var paths = ManifestPaths(backup).ToList();
        foreach (var path in paths)
        {
            await storage.WriteObjectAsync(backup.Target, path, content, cancellationToken);
        }

        _logger.Information("Wrote backup storage manifest for backup {BackupId} to {ManifestCount} storage path(s).", backup.Id, paths.Count);
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
            .Where(x => x.EndsWith(ManifestRelativePath, StringComparison.Ordinal))
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
            if (unique.TryGetValue(manifest.Backup.Id, out var existing))
            {
                skipped++;
                if (manifest.GeneratedAt > existing.Manifest.GeneratedAt)
                {
                    unique[manifest.Backup.Id] = (source, manifest);
                }
            }
            else
            {
                unique.Add(manifest.Backup.Id, (source, manifest));
            }
        }

        var imported = 0;
        var updated = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var (source, manifest) in unique.Values)
            {
                var existed = await db.Backups.AnyAsync(x => x.Id == manifest.Backup.Id, cancellationToken);
                await ImportManifestAsync(scanTarget, manifest, cancellationToken);
                if (existed)
                {
                    updated++;
                }
                else
                {
                    imported++;
                }

                items.Add(new BackupMetadataRecoveryItem(
                    manifest.Backup.Id,
                    manifest.Backup.Status,
                    source,
                    !existed,
                    existed,
                    existed ? "Updated existing backup metadata." : "Imported backup metadata."));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await audit.RecordAsync("backup-metadata-recovered", AuditEntityType.Backup, null, new
        {
            scanTargetId = scanTarget.Id,
            scannedManifestCount = candidates.Count,
            importedBackupCount = imported,
            updatedBackupCount = updated,
            skippedManifestCount = skipped,
            errors
        });

        return new BackupMetadataRecoveryResult(candidates.Count, imported, updated, skipped, items, errors);
    }

    private async Task ImportManifestAsync(BackupTargetEntity scanTarget, BackupStorageManifestV1 manifest, CancellationToken cancellationToken)
    {
        var target = await db.BackupTargets.FirstOrDefaultAsync(x => x.Id == manifest.Target.Id, cancellationToken);
        if (target is null)
        {
            target = new BackupTargetEntity { Id = manifest.Target.Id };
            db.BackupTargets.Add(target);
        }
        target.Name = manifest.Target.Name;
        target.Type = manifest.Target.Type;
        target.Endpoint = manifest.Target.S3.Endpoint;
        target.Region = manifest.Target.S3.Region;
        target.Bucket = manifest.Target.S3.Bucket;
        target.PathPrefix = manifest.Target.S3.PathPrefix;
        target.ForcePathStyle = manifest.Target.S3.ForcePathStyle;
        target.IsDeleted = manifest.Target.IsDeleted;
        target.CreatedAt = manifest.Target.CreatedAt;
        target.UpdatedAt = manifest.Target.UpdatedAt;
        target.DeletedAt = manifest.Target.DeletedAt;
        target.EncryptedAccessKey = scanTarget.EncryptedAccessKey;
        target.EncryptedAccessKeyKeyId = scanTarget.EncryptedAccessKeyKeyId;
        target.EncryptedSecretKey = scanTarget.EncryptedSecretKey;
        target.EncryptedSecretKeyKeyId = scanTarget.EncryptedSecretKeyKeyId;

        var cluster = await db.ClickHouseClusters.Include(x => x.AccessNodes).FirstOrDefaultAsync(x => x.Id == manifest.SourceCluster.Id, cancellationToken);
        if (cluster is null)
        {
            cluster = new ClickHouseClusterEntity { Id = manifest.SourceCluster.Id };
            db.ClickHouseClusters.Add(cluster);
        }
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

        if (manifest.Policy is { } policyManifest)
        {
            var policy = await db.BackupPolicies.FirstOrDefaultAsync(x => x.Id == policyManifest.Id, cancellationToken);
            if (policy is null)
            {
                policy = new BackupPolicyEntity { Id = policyManifest.Id };
                db.BackupPolicies.Add(policy);
            }
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
            policy.IsDeleted = policyManifest.IsDeleted;
            policy.CreatedAt = policyManifest.CreatedAt;
            policy.UpdatedAt = policyManifest.UpdatedAt;
            policy.DeletedAt = policyManifest.DeletedAt;
        }

        if (manifest.Schedule is { } scheduleManifest)
        {
            var schedule = await db.BackupSchedules.FirstOrDefaultAsync(x => x.Id == scheduleManifest.Id, cancellationToken);
            if (schedule is null)
            {
                schedule = new BackupScheduleEntity { Id = scheduleManifest.Id };
                db.BackupSchedules.Add(schedule);
            }
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

        var backup = await db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).FirstOrDefaultAsync(x => x.Id == manifest.Backup.Id, cancellationToken);
        if (backup is null)
        {
            backup = new BackupEntity { Id = manifest.Backup.Id };
            db.Backups.Add(backup);
        }
        backup.TriggerType = manifest.Backup.TriggerType;
        backup.Status = manifest.Backup.Status;
        backup.BackupType = manifest.Backup.BackupType;
        backup.SourceClusterId = manifest.Backup.SourceClusterId;
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
        backup.Error = manifest.Backup.Error;
        backup.FailureReason = manifest.Backup.FailureReason;
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
            table.S3Path = tableManifest.S3Path;
            table.BackupSizeBytes = tableManifest.BackupSizeBytes;
            table.Status = tableManifest.Status;
            table.ClickHouseOperationId = tableManifest.ClickHouseOperationId;
            table.ClickHouseStatus = tableManifest.ClickHouseStatus;
            table.StartedAt = tableManifest.StartedAt;
            table.CompletedAt = tableManifest.CompletedAt;
            table.Error = tableManifest.Error;

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
                shard.S3Path = shardManifest.S3Path;
                shard.BackupSizeBytes = shardManifest.BackupSizeBytes;
                shard.Status = shardManifest.Status;
                shard.ClickHouseOperationId = shardManifest.ClickHouseOperationId;
                shard.ClickHouseStatus = shardManifest.ClickHouseStatus;
                shard.StartedAt = shardManifest.StartedAt;
                shard.CompletedAt = shardManifest.CompletedAt;
                shard.Error = shardManifest.Error;
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

    public static IEnumerable<string> ManifestPaths(BackupEntity backup)
    {
        foreach (var prefix in backup.Tables.Select(x => x.S3Path)
                     .Concat(backup.Tables.SelectMany(x => x.Shards.Select(s => s.S3Path)))
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal))
        {
            yield return JoinPath(prefix, ManifestRelativePath);
        }
    }

    private static string NormalizeManifestPath(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path is required.");
        }
        var path = backupPath.Trim().TrimStart('/');
        return path.EndsWith(ManifestRelativePath, StringComparison.Ordinal)
            ? path
            : JoinPath(path, ManifestRelativePath);
    }

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
            schemas,
            backup.Tables.OrderBy(x => x.Database).ThenBy(x => x.Table).Select(ToManifestTable).ToList());
    }

    private static BackupStorageManifestTargetV1 ToManifestTarget(BackupTargetEntity target) =>
        new(target.Id, target.Name, target.Type, new S3TargetSettingsDto(target.Endpoint, target.Region, target.Bucket, target.PathPrefix, target.ForcePathStyle), target.IsDeleted, target.CreatedAt, target.UpdatedAt, target.DeletedAt);

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
            policy.DeletedAt);

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
            table.S3Path,
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
            shard.S3Path,
            shard.BackupSizeBytes,
            shard.Status,
            shard.ClickHouseOperationId,
            shard.ClickHouseStatus,
            shard.StartedAt,
            shard.CompletedAt,
            shard.Error);

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
}



