using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Services;

public interface IExportImportService
{
    Task<ExportEnvelope> ExportAsync(bool configOnly);
    Task ImportAsync(ExportEnvelope envelope, bool configOnly);
}

public sealed class ExportImportService(ChoboDbContext db, IActorContext actor) : IExportImportService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<ExportEnvelope> ExportAsync(bool configOnly)
    {
        var clusters = await db.ClickHouseClusters.Include(x => x.AccessNodes).ToListAsync();
        var targets = await db.BackupTargets.ToListAsync();
        var policies = await db.BackupPolicies.ToListAsync();
        var schedules = await db.BackupSchedules.ToListAsync();
        var schemaDefinitions = configOnly ? [] : await db.SchemaDefinitions.ToListAsync();
        var backups = configOnly ? [] : await db.Backups.ToListAsync();
        var backupTables = configOnly ? [] : await db.BackupTables.ToListAsync();
        var backupTableShards = configOnly ? [] : await db.BackupTableShards.ToListAsync();
        var restores = configOnly ? [] : await db.Restores.ToListAsync();
        var restoreTables = configOnly ? [] : await db.RestoreTables.ToListAsync();
        var restoreTableShards = configOnly ? [] : await db.RestoreTableShards.ToListAsync();

        var payload = new ExportPayload(
            [],
            [],
            clusters.Select(x => new ClusterExport(x.Id, x.Name, x.Mode, x.ClickHouseClusterName, x.AccessNodes.Select(n => new AccessNodeDto(n.Id, n.Host, n.Port, n.UseTls)).ToList(), x.EncryptedUserName, x.EncryptedUserNameKeyId, x.EncryptedPassword, x.EncryptedPasswordKeyId, x.BackupRestoreMaxDop, x.NodeMaxDopDefault, DeserializeNodeOverrides(x.NodeMaxDopOverridesJson), x.ShardMaxDopDefault, DeserializeShardOverrides(x.ShardMaxDopOverridesJson), ClickHouseAdvancedSettings.Deserialize(x.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup), ClickHouseAdvancedSettings.Deserialize(x.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore), x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            targets.Select(x => new BackupTargetExport(x.Id, x.Name, x.Type, ReadJsonDictionary(x.SettingsJson), ReadJsonDictionary(x.SecretsJson), x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            policies.Select(x => new BackupPolicyExport(
                x.Id,
                x.Name,
                x.SourceClusterId,
                x.TargetId,
                x.ContentMode,
                x.SelectorJsonVersion,
                JsonSerializer.Deserialize<PolicySelector>(x.SelectorJson, JsonOptions)!,
                x.FullRetentionMinutes is null && x.IncrementalRetentionMinutes is null ? null : new BackupRetentionDto(x.FullRetentionMinutes, x.IncrementalRetentionMinutes, x.MinBackupsToKeep, x.MinFullBackupsToKeep),
                x.FailedBackupRetentionMode,
                ClickHouseAdvancedSettings.Deserialize(x.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup),
                ClickHouseAdvancedSettings.Deserialize(x.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore),
                x.IsSystemDefault,
                x.IsDeleted,
                x.CreatedAt,
                x.UpdatedAt,
                x.DeletedAt)).ToList(),
            schedules.Select(x => new BackupScheduleExport(x.Id, x.Name, x.PolicyId, x.BackupType, x.CronExpression, x.TimeZoneId, x.IsEnabled, x.MissedRunGracePeriod, x.Description, x.IsSystemDefault, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            schemaDefinitions.Select(x => new SchemaDefinitionExport(x.Id, x.SchemaHash, x.Database, x.Table, x.Engine, x.CreateTableSql, x.ColumnsJson, x.CreatedAt)).ToList(),
            backups.Select(x => new BackupExport(x.Id, x.TriggerType, x.Status, x.BackupType, x.ContentMode, x.SourceClusterId, x.TargetId, x.PolicyId, x.ScheduleId, x.ManualRequestJson, x.RequestedByUserId, x.RequestedByName, x.CreatedAt, x.QueuedAt, x.StartedAt, x.CompletedAt, x.Error, x.FailureReason, x.IsPinned, x.PinnedAt, x.PinnedByUserId, x.PinnedByName, x.DeletionReason, x.DeletionRequestedAt, x.DeletionStartedAt, x.DeletedAt, x.DeletionError, x.DeletionAttemptCount, ClickHouseAdvancedSettings.Deserialize(x.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup))).ToList(),
            backupTables.Select(x => new BackupTableExport(x.Id, x.BackupId, x.EffectiveBackupType, x.ParentFullBackupId, x.ParentFullBackupTableId, x.Database, x.Table, x.Engine, x.DataBackedUp, x.SchemaDefinitionId, x.StoragePath, x.BackupSizeBytes, x.Status, x.ClickHouseOperationId, x.ClickHouseStatus, x.StartedAt, x.CompletedAt, x.Error)).ToList(),
            backupTableShards.Select(x => new BackupTableShardExport(x.Id, x.BackupTableId, x.EffectiveBackupType, x.ParentFullBackupId, x.ParentFullBackupTableShardId, x.SourceShardNumber, x.SourceShardName, x.ReplicaNumber, x.Host, x.Port, x.UseTls, x.StoragePath, x.BackupSizeBytes, x.Status, x.ClickHouseOperationId, x.ClickHouseStatus, x.StartedAt, x.CompletedAt, x.Error)).ToList(),
            restores.Select(x => new RestoreExport(x.Id, x.BackupId, x.TargetClusterId, x.Status, x.Append, x.AllowSchemaMismatch, x.Layout, x.SourceShard, x.TargetShard, x.RequestJson, x.RequestedByUserId, x.RequestedByName, x.CreatedAt, x.QueuedAt, x.StartedAt, x.CompletedAt, x.Error, x.FailureReason, ClickHouseAdvancedSettings.Deserialize(x.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore))).ToList(),
            restoreTables.Select(x => new RestoreTableExport(x.Id, x.RestoreId, x.BackupTableId, x.SourceDatabase, x.SourceTable, x.TargetDatabase, x.TargetTable, x.Append, x.AllowSchemaMismatch, x.SchemaOnly, x.Status, x.ClickHouseOperationId, x.ClickHouseStatus, x.Warning, x.StartedAt, x.CompletedAt, x.Error)).ToList(),
            restoreTableShards.Select(x => new RestoreTableShardExport(x.Id, x.RestoreTableId, x.BackupTableShardId, x.SourceShardNumber, x.TargetShardNumber, x.TargetShardName, x.TargetReplicaNumber, x.TargetHost, x.TargetPort, x.TargetUseTls, x.LayoutRole, x.RestoreDatabase, x.RestoreTableName, x.Status, x.ClickHouseOperationId, x.ClickHouseStatus, x.Warning, x.StartedAt, x.CompletedAt, x.Error)).ToList());

        return new ExportEnvelope(ChoboApi.ExportVersion, ChoboApi.SchemaVersion, DateTimeOffset.UtcNow, ChoboApi.ProductVersion, payload);
    }

    public async Task ImportAsync(ExportEnvelope envelope, bool configOnly)
    {
        if (envelope.ExportVersion != ChoboApi.ExportVersion)
        {
            throw new InvalidOperationException($"Unsupported export version {envelope.ExportVersion}.");
        }
        if (envelope.SchemaVersion > ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported schema version {envelope.SchemaVersion}.");
        }
        if (configOnly && (await db.Backups.AnyAsync() || await db.Restores.AnyAsync()))
        {
            throw new InvalidOperationException("Config import cannot run while backup or restore history exists. Use data import to restore operational state, or start from an empty server.");
        }

        var import = BuildImportPlan(envelope.Data, configOnly);

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.RestoreTableShards.ExecuteDeleteAsync();
            await db.RestoreTables.ExecuteDeleteAsync();
            await db.Restores.ExecuteDeleteAsync();
            await db.BackupTableShards.ExecuteDeleteAsync();
            await db.BackupTables.ExecuteDeleteAsync();
            await db.Backups.ExecuteDeleteAsync();
            await db.SchemaDefinitions.ExecuteDeleteAsync();
            await db.BackupSchedules.ExecuteDeleteAsync();
            await db.BackupPolicies.ExecuteDeleteAsync();
            await db.BackupTargets.ExecuteDeleteAsync();
            await db.ClickHouseAccessNodes.ExecuteDeleteAsync();
            await db.ClickHouseClusters.ExecuteDeleteAsync();

            var importedAt = DateTimeOffset.UtcNow;
            var inFlightImportedAsFailed = CountInFlightOperationalRows(import.Payload);

            foreach (var cluster in import.Payload.Clusters)
            {
                db.ClickHouseClusters.Add(new ClickHouseClusterEntity
                {
                    Id = cluster.Id,
                    Name = cluster.Name,
                    Mode = cluster.Mode,
                    EncryptedUserName = null,
                    EncryptedUserNameKeyId = null,
                    EncryptedPassword = null,
                    EncryptedPasswordKeyId = null,
                    BackupRestoreMaxDop = cluster.BackupRestoreMaxDop ?? 3,
                    NodeMaxDopDefault = cluster.NodeMaxDopDefault,
                    NodeMaxDopOverridesJson = JsonSerializer.Serialize(cluster.NodeMaxDopOverrides ?? [], JsonOptions),
                    ShardMaxDopDefault = cluster.ShardMaxDopDefault,
                    ShardMaxDopOverridesJson = JsonSerializer.Serialize(cluster.ShardMaxDopOverrides ?? [], JsonOptions),
                    ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(cluster.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup),
                    ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(cluster.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore),
                    ClickHouseClusterName = cluster.ClickHouseClusterName,
                    IsDeleted = cluster.IsDeleted,
                    CreatedAt = cluster.CreatedAt,
                    UpdatedAt = cluster.UpdatedAt,
                    DeletedAt = cluster.DeletedAt,
                    AccessNodes = cluster.AccessNodes.Select(n => new ClickHouseAccessNodeEntity { Id = n.Id, Host = n.Host, Port = n.Port, UseTls = n.UseTls }).ToList()
                });
            }
            db.BackupTargets.AddRange(import.Payload.BackupTargets.Select(ToImportedTarget));
            db.BackupPolicies.AddRange(import.Payload.BackupPolicies.Select(x => new BackupPolicyEntity
            {
                Id = x.Id,
                Name = x.Name,
                SourceClusterId = x.SourceClusterId,
                TargetId = x.TargetId,
                ContentMode = x.ContentMode,
                SelectorJsonVersion = x.SelectorJsonVersion,
                SelectorJson = JsonSerializer.Serialize(x.Selector, JsonOptions),
                FullRetentionMinutes = x.Retention?.FullRetentionMinutes,
                IncrementalRetentionMinutes = x.Retention?.IncrementalRetentionMinutes,
                MinBackupsToKeep = x.Retention?.MinBackupsToKeep ?? 0,
                MinFullBackupsToKeep = x.Retention?.MinFullBackupsToKeep ?? 0,
                FailedBackupRetentionMode = x.FailedBackupRetentionMode,
                ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(x.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup),
                ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(x.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore),
                IsSystemDefault = x.IsSystemDefault,
                IsDeleted = x.IsDeleted,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt,
                DeletedAt = x.DeletedAt
            }));
            db.BackupSchedules.AddRange(import.Payload.BackupSchedules.Select(x => new BackupScheduleEntity { Id = x.Id, Name = x.Name, PolicyId = x.PolicyId, BackupType = x.BackupType, CronExpression = x.CronExpression, TimeZoneId = x.TimeZoneId, IsEnabled = x.IsEnabled, MissedRunGracePeriod = x.MissedRunGracePeriod, Description = x.Description, IsSystemDefault = x.IsSystemDefault, IsDeleted = x.IsDeleted, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, DeletedAt = x.DeletedAt }));

            if (!configOnly)
            {
                db.SchemaDefinitions.AddRange(import.Payload.SchemaDefinitions.Select(x => new SchemaDefinitionEntity { Id = x.Id, SchemaHash = x.SchemaHash, Database = x.Database, Table = x.Table, Engine = x.Engine, CreateTableSql = x.CreateTableSql, ColumnsJson = x.ColumnsJson, CreatedAt = x.CreatedAt }));
                db.Backups.AddRange(import.Payload.Backups.Select(x => new BackupEntity { Id = x.Id, TriggerType = x.TriggerType, Status = NormalizeBackupRunStatus(x.Status), BackupType = x.BackupType, ContentMode = x.ContentMode, SourceClusterId = x.SourceClusterId, TargetId = x.TargetId, PolicyId = x.PolicyId, ScheduleId = x.ScheduleId, ManualRequestJson = x.ManualRequestJson, ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(x.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup), RequestedByUserId = null, RequestedByName = x.RequestedByName, CreatedAt = x.CreatedAt, QueuedAt = x.QueuedAt, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is BackupRunStatus.Queued or BackupRunStatus.Running, x.CompletedAt, importedAt), Error = x.Error, FailureReason = NormalizeFailureReason(x.Status is BackupRunStatus.Queued or BackupRunStatus.Running, x.FailureReason), IsPinned = x.IsPinned, PinnedAt = x.PinnedAt, PinnedByUserId = null, PinnedByName = x.PinnedByName, DeletionReason = x.DeletionReason, DeletionRequestedAt = x.DeletionRequestedAt, DeletionStartedAt = x.DeletionStartedAt, DeletedAt = x.DeletedAt, DeletionError = x.DeletionError, DeletionAttemptCount = x.DeletionAttemptCount }));
                db.BackupTables.AddRange(import.Payload.BackupTables.Select(x => new BackupTableEntity { Id = x.Id, BackupId = x.BackupId, EffectiveBackupType = x.EffectiveBackupType, ParentFullBackupId = x.ParentFullBackupId, ParentFullBackupTableId = x.ParentFullBackupTableId, Database = x.Database, Table = x.Table, Engine = x.Engine, DataBackedUp = x.DataBackedUp, SchemaDefinitionId = x.SchemaDefinitionId, StoragePath = ExportStoragePath(x), BackupSizeBytes = x.BackupSizeBytes, Status = NormalizeBackupTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.Error) }));
                db.BackupTableShards.AddRange(import.Payload.BackupTableShards.Select(x => new BackupTableShardEntity { Id = x.Id, BackupTableId = x.BackupTableId, EffectiveBackupType = x.EffectiveBackupType, ParentFullBackupId = x.ParentFullBackupId, ParentFullBackupTableShardId = x.ParentFullBackupTableShardId, SourceShardNumber = x.SourceShardNumber, SourceShardName = x.SourceShardName, ReplicaNumber = x.ReplicaNumber, Host = x.Host, Port = x.Port, UseTls = x.UseTls, StoragePath = ExportStoragePath(x), BackupSizeBytes = x.BackupSizeBytes, Status = NormalizeBackupTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.Error) }));
                db.Restores.AddRange(import.Payload.Restores.Select(x => new RestoreEntity { Id = x.Id, BackupId = x.BackupId, TargetClusterId = x.TargetClusterId, Status = NormalizeRestoreRunStatus(x.Status), Append = x.Append, AllowSchemaMismatch = x.AllowSchemaMismatch, Layout = x.Layout, SourceShard = x.SourceShard, TargetShard = x.TargetShard, RequestJson = x.RequestJson, ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(x.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore), RequestedByUserId = null, RequestedByName = x.RequestedByName, CreatedAt = x.CreatedAt, QueuedAt = x.QueuedAt, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running, x.CompletedAt, importedAt), Error = x.Error, FailureReason = NormalizeFailureReason(x.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running, x.FailureReason) }));
                db.RestoreTables.AddRange(import.Payload.RestoreTables.Select(x => new RestoreTableEntity { Id = x.Id, RestoreId = x.RestoreId, BackupTableId = x.BackupTableId, SourceDatabase = x.SourceDatabase, SourceTable = x.SourceTable, TargetDatabase = x.TargetDatabase, TargetTable = x.TargetTable, Append = x.Append, AllowSchemaMismatch = x.AllowSchemaMismatch, SchemaOnly = x.SchemaOnly, Status = NormalizeRestoreTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, Warning = x.Warning, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.Error) }));
                db.RestoreTableShards.AddRange(import.Payload.RestoreTableShards.Select(x => new RestoreTableShardEntity { Id = x.Id, RestoreTableId = x.RestoreTableId, BackupTableShardId = x.BackupTableShardId, SourceShardNumber = x.SourceShardNumber, TargetShardNumber = x.TargetShardNumber, TargetShardName = x.TargetShardName, TargetReplicaNumber = x.TargetReplicaNumber, TargetHost = x.TargetHost, TargetPort = x.TargetPort, TargetUseTls = x.TargetUseTls, LayoutRole = x.LayoutRole, RestoreDatabase = x.RestoreDatabase, RestoreTableName = x.RestoreTableName, Status = NormalizeRestoreTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, Warning = x.Warning, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.Error) }));
            }

            db.AuditEntries.Add(new AuditEntryEntity
            {
                ActorUserId = actor.UserId,
                ActorName = actor.ActorName,
                Action = "import",
                EntityType = AuditEntityTypes.ToStorageValue(configOnly ? AuditEntityType.Config : AuditEntityType.Data),
                Details = AuditDetails.Serialize(new
                {
                    envelope.ExportVersion,
                    envelope.SchemaVersion,
                    credentialsImportedAsEmpty = true,
                    inFlightImportedAsFailed,
                    skippedRows = import.SkippedRows,
                    ignoredUsers = envelope.Data.Users.Count,
                    ignoredAccessTokens = envelope.Data.AccessTokens.Count,
                    importedBackups = configOnly ? 0 : import.Payload.Backups.Count,
                    importedRestores = configOnly ? 0 : import.Payload.Restores.Count
                })
            });

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }



    private static string ExportStoragePath(BackupTableExport table) =>
        table.StoragePath ?? table.S3Path ?? throw new InvalidOperationException($"Backup table export '{table.Id}' is missing storagePath.");

    private static string ExportStoragePath(BackupTableShardExport shard) =>
        shard.StoragePath ?? shard.S3Path ?? throw new InvalidOperationException($"Backup table shard export '{shard.Id}' is missing storagePath.");
    private static BackupTargetEntity ToImportedTarget(BackupTargetExport target) =>
        new()
        {
            Id = target.Id,
            Name = target.Name,
            Type = NormalizeTargetType(target.Type),
            SettingsJson = JsonSerializer.Serialize(TargetSettings(target), JsonOptions),
            SecretsJson = "{}",
            IsDeleted = target.IsDeleted,
            CreatedAt = target.CreatedAt,
            UpdatedAt = target.UpdatedAt,
            DeletedAt = target.DeletedAt
        };

    private static string NormalizeTargetType(string? type) =>
        string.IsNullOrWhiteSpace(type) || string.Equals(type, "S3", StringComparison.OrdinalIgnoreCase)
            ? StorageProviderTypes.S3
            : type;

    private static IReadOnlyDictionary<string, JsonElement> TargetSettings(BackupTargetExport target) =>
        target.Settings is not null
            ? CloneDictionary(target.Settings)
            : target.S3 is not null
                ? ReadJsonDictionary(JsonSerializer.Serialize(target.S3, JsonOptions))
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, JsonElement> ReadJsonDictionary(string json)
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
    private const string ImportedInFlightFailureReason = "Imported in-flight operation was marked failed; credentials and external operation state are local to the source server.";

    private static BackupRunStatus NormalizeBackupRunStatus(BackupRunStatus status) =>
        status is BackupRunStatus.Queued or BackupRunStatus.Running ? BackupRunStatus.Failed : status;

    private static BackupTableStatus NormalizeBackupTableStatus(BackupTableStatus status) =>
        status is BackupTableStatus.Queued or BackupTableStatus.Running ? BackupTableStatus.Failed : status;

    private static RestoreRunStatus NormalizeRestoreRunStatus(RestoreRunStatus status) =>
        status is RestoreRunStatus.Queued or RestoreRunStatus.Running ? RestoreRunStatus.Failed : status;

    private static RestoreTableStatus NormalizeRestoreTableStatus(RestoreTableStatus status) =>
        status is RestoreTableStatus.Queued or RestoreTableStatus.Running ? RestoreTableStatus.Failed : status;

    private static DateTimeOffset? NormalizeCompletedAt(bool importedInFlight, DateTimeOffset? completedAt, DateTimeOffset importedAt) =>
        importedInFlight && completedAt is null ? importedAt : completedAt;

    private static string? NormalizeFailureReason(bool importedInFlight, string? failureReason) =>
        importedInFlight && string.IsNullOrWhiteSpace(failureReason) ? ImportedInFlightFailureReason : failureReason;

    private static string? NormalizeError(bool importedInFlight, string? error) =>
        importedInFlight && string.IsNullOrWhiteSpace(error) ? ImportedInFlightFailureReason : error;

    private static int CountInFlightOperationalRows(ExportPayload data) =>
        data.Backups.Count(x => x.Status is BackupRunStatus.Queued or BackupRunStatus.Running) +
        data.BackupTables.Count(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running) +
        data.BackupTableShards.Count(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running) +
        data.Restores.Count(x => x.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running) +
        data.RestoreTables.Count(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running) +
        data.RestoreTableShards.Count(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running);
    private static bool HasOperationalRows(ExportPayload data) =>
        data.SchemaDefinitions.Count > 0 ||
        data.Backups.Count > 0 ||
        data.BackupTables.Count > 0 ||
        data.BackupTableShards.Count > 0 ||
        data.Restores.Count > 0 ||
        data.RestoreTables.Count > 0 ||
        data.RestoreTableShards.Count > 0;
    private static ImportPlan BuildImportPlan(ExportPayload data, bool configOnly)
    {
        if (configOnly && HasOperationalRows(data))
        {
            data = data with
            {
                SchemaDefinitions = [],
                Backups = [],
                BackupTables = [],
                BackupTableShards = [],
                Restores = [],
                RestoreTables = [],
                RestoreTableShards = []
            };
        }

        var skipped = data.Users.Count + data.AccessTokens.Count;
        var clusters = DistinctById(data.Clusters, x => x.Id, ref skipped);
        var targets = DistinctById(data.BackupTargets, x => x.Id, ref skipped);
        var clusterIds = clusters.Select(x => x.Id).ToHashSet();
        var targetIds = targets.Select(x => x.Id).ToHashSet();

        var distinctPolicies = DistinctById(data.BackupPolicies, x => x.Id, ref skipped);
        var policies = distinctPolicies
            .Where(x => clusterIds.Contains(x.SourceClusterId))
            .Where(x => x.TargetId is null || targetIds.Contains(x.TargetId.Value))
            .Where(x => x.ContentMode != BackupContentMode.SchemaAndData || x.TargetId is not null)
            .ToList();
        skipped += distinctPolicies.Count - policies.Count;

        var policyIds = policies.Select(x => x.Id).ToHashSet();
        var distinctSchedules = DistinctById(data.BackupSchedules, x => x.Id, ref skipped);
        var schedules = distinctSchedules.Where(x => policyIds.Contains(x.PolicyId)).ToList();
        skipped += distinctSchedules.Count - schedules.Count;

        var scheduleIds = schedules.Select(x => x.Id).ToHashSet();
        var schemaDefinitions = DistinctById(data.SchemaDefinitions, x => x.Id, ref skipped);
        var schemaDefinitionIds = schemaDefinitions.Select(x => x.Id).ToHashSet();

        var distinctBackups = DistinctById(data.Backups, x => x.Id, ref skipped);
        var backups = distinctBackups
            .Where(x => clusterIds.Contains(x.SourceClusterId))
            .Where(x => x.TargetId is null || targetIds.Contains(x.TargetId.Value))
            .Where(x => x.ContentMode != BackupContentMode.SchemaAndData || x.TargetId is not null)
            .Where(x => x.PolicyId is null || policyIds.Contains(x.PolicyId.Value))
            .Where(x => x.ScheduleId is null || scheduleIds.Contains(x.ScheduleId.Value))
            .ToList();
        skipped += distinctBackups.Count - backups.Count;

        var backupIds = backups.Select(x => x.Id).ToHashSet();
        var distinctBackupTables = DistinctById(data.BackupTables, x => x.Id, ref skipped);
        var backupTables = distinctBackupTables
            .Where(x => backupIds.Contains(x.BackupId))
            .Where(x => x.SchemaDefinitionId is null || schemaDefinitionIds.Contains(x.SchemaDefinitionId.Value))
            .Where(x => x.ParentFullBackupId is null || backupIds.Contains(x.ParentFullBackupId.Value))
            .ToList();
        var backupTableIds = backupTables.Select(x => x.Id).ToHashSet();
        backupTables = backupTables
            .Where(x => x.ParentFullBackupTableId is null || backupTableIds.Contains(x.ParentFullBackupTableId.Value))
            .ToList();
        skipped += distinctBackupTables.Count - backupTables.Count;

        backupTableIds = backupTables.Select(x => x.Id).ToHashSet();
        var distinctBackupShards = DistinctById(data.BackupTableShards, x => x.Id, ref skipped);
        var backupShards = distinctBackupShards
            .Where(x => backupTableIds.Contains(x.BackupTableId))
            .Where(x => x.ParentFullBackupId is null || backupIds.Contains(x.ParentFullBackupId.Value))
            .ToList();
        var backupShardIds = backupShards.Select(x => x.Id).ToHashSet();
        backupShards = backupShards
            .Where(x => x.ParentFullBackupTableShardId is null || backupShardIds.Contains(x.ParentFullBackupTableShardId.Value))
            .ToList();
        skipped += distinctBackupShards.Count - backupShards.Count;

        backupShardIds = backupShards.Select(x => x.Id).ToHashSet();
        var distinctRestores = DistinctById(data.Restores, x => x.Id, ref skipped);
        var restores = distinctRestores
            .Where(x => backupIds.Contains(x.BackupId))
            .Where(x => clusterIds.Contains(x.TargetClusterId))
            .ToList();
        skipped += distinctRestores.Count - restores.Count;

        var restoreIds = restores.Select(x => x.Id).ToHashSet();
        var distinctRestoreTables = DistinctById(data.RestoreTables, x => x.Id, ref skipped);
        var restoreTables = distinctRestoreTables
            .Where(x => restoreIds.Contains(x.RestoreId))
            .Where(x => backupTableIds.Contains(x.BackupTableId))
            .ToList();
        skipped += distinctRestoreTables.Count - restoreTables.Count;

        var restoreTableIds = restoreTables.Select(x => x.Id).ToHashSet();
        var distinctRestoreShards = DistinctById(data.RestoreTableShards, x => x.Id, ref skipped);
        var restoreShards = distinctRestoreShards
            .Where(x => restoreTableIds.Contains(x.RestoreTableId))
            .Where(x => backupShardIds.Contains(x.BackupTableShardId))
            .ToList();
        skipped += distinctRestoreShards.Count - restoreShards.Count;

        return new ImportPlan(data with
        {
            Users = [],
            AccessTokens = [],
            Clusters = clusters,
            BackupTargets = targets,
            BackupPolicies = policies,
            BackupSchedules = schedules,
            SchemaDefinitions = schemaDefinitions,
            Backups = backups,
            BackupTables = backupTables,
            BackupTableShards = backupShards,
            Restores = restores,
            RestoreTables = restoreTables,
            RestoreTableShards = restoreShards
        }, Math.Max(0, skipped));
    }

    private static List<T> DistinctById<T>(IEnumerable<T> rows, Func<T, Guid> getId, ref int skipped)
    {
        var seen = new HashSet<Guid>();
        var result = new List<T>();
        foreach (var row in rows)
        {
            if (seen.Add(getId(row)))
            {
                result.Add(row);
            }
            else
            {
                skipped++;
            }
        }
        return result;
    }

    private sealed record ImportPlan(ExportPayload Payload, int SkippedRows);
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
