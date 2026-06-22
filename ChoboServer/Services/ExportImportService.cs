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
        var users = await db.Users.ToListAsync();
        var tokens = await db.AccessTokens.ToListAsync();
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
            users.Select(x => new UserExport(x.Id, x.UserName, x.IsActive, x.CreatedAt, x.DeactivatedAt)).ToList(),
            tokens.Select(x => new AccessTokenExport(x.Id, x.UserId, x.Name, x.TokenHash, x.TokenLookupHash, x.Salt, x.IsActive, x.CreatedAt, x.DeactivatedAt)).ToList(),
            clusters.Select(x => new ClusterExport(x.Id, x.Name, x.Mode, x.ClickHouseClusterName, x.AccessNodes.Select(n => new AccessNodeDto(n.Id, n.Host, n.Port, n.UseTls)).ToList(), x.EncryptedUserName, x.EncryptedUserNameKeyId, x.EncryptedPassword, x.EncryptedPasswordKeyId, x.BackupRestoreMaxDop, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            targets.Select(x => new BackupTargetExport(x.Id, x.Name, x.Type, new S3TargetSettingsDto(x.Endpoint, x.Region, x.Bucket, x.PathPrefix, x.ForcePathStyle), x.EncryptedAccessKey, x.EncryptedAccessKeyKeyId, x.EncryptedSecretKey, x.EncryptedSecretKeyKeyId, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
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
                x.IsSystemDefault,
                x.IsDeleted,
                x.CreatedAt,
                x.UpdatedAt,
                x.DeletedAt)).ToList(),
            schedules.Select(x => new BackupScheduleExport(x.Id, x.Name, x.PolicyId, x.BackupType, x.CronExpression, x.TimeZoneId, x.IsEnabled, x.MissedRunGracePeriod, x.Description, x.IsSystemDefault, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            schemaDefinitions.Select(x => new SchemaDefinitionExport(x.Id, x.SchemaHash, x.Database, x.Table, x.Engine, x.CreateTableSql, x.ColumnsJson, x.CreatedAt)).ToList(),
            backups.Select(x => new BackupExport(x.Id, x.TriggerType, x.Status, x.BackupType, x.ContentMode, x.SourceClusterId, x.TargetId, x.PolicyId, x.ScheduleId, x.ManualRequestJson, x.RequestedByUserId, x.RequestedByName, x.CreatedAt, x.QueuedAt, x.StartedAt, x.CompletedAt, x.Error, x.FailureReason, x.IsPinned, x.PinnedAt, x.PinnedByUserId, x.PinnedByName, x.DeletionReason, x.DeletionRequestedAt, x.DeletionStartedAt, x.DeletedAt, x.DeletionError, x.DeletionAttemptCount)).ToList(),
            backupTables.Select(x => new BackupTableExport(x.Id, x.BackupId, x.EffectiveBackupType, x.ParentFullBackupId, x.ParentFullBackupTableId, x.Database, x.Table, x.Engine, x.DataBackedUp, x.SchemaDefinitionId, x.S3Path, x.BackupSizeBytes, x.Status, x.ClickHouseOperationId, x.ClickHouseStatus, x.StartedAt, x.CompletedAt, x.Error)).ToList(),
            backupTableShards.Select(x => new BackupTableShardExport(x.Id, x.BackupTableId, x.EffectiveBackupType, x.ParentFullBackupId, x.ParentFullBackupTableShardId, x.SourceShardNumber, x.SourceShardName, x.ReplicaNumber, x.Host, x.Port, x.UseTls, x.S3Path, x.BackupSizeBytes, x.Status, x.ClickHouseOperationId, x.ClickHouseStatus, x.StartedAt, x.CompletedAt, x.Error)).ToList(),
            restores.Select(x => new RestoreExport(x.Id, x.BackupId, x.TargetClusterId, x.Status, x.Append, x.AllowSchemaMismatch, x.Layout, x.SourceShard, x.TargetShard, x.RequestJson, x.RequestedByUserId, x.RequestedByName, x.CreatedAt, x.QueuedAt, x.StartedAt, x.CompletedAt, x.Error, x.FailureReason)).ToList(),
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

        ValidateImportPayload(envelope, configOnly);

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
            await db.AccessTokens.ExecuteDeleteAsync();
            await db.Users.ExecuteDeleteAsync();

            var importedAt = DateTimeOffset.UtcNow;
            var inFlightImportedAsFailed = CountInFlightOperationalRows(envelope.Data);

            db.Users.AddRange(envelope.Data.Users.Select(x => new UserEntity { Id = x.Id, UserName = x.UserName, IsActive = x.IsActive, CreatedAt = x.CreatedAt, DeactivatedAt = x.DeactivatedAt }));
            db.AccessTokens.AddRange(envelope.Data.AccessTokens.Select(x => new AccessTokenEntity { Id = x.Id, UserId = x.UserId, Name = x.Name, TokenHash = x.TokenHash, TokenLookupHash = x.TokenLookupHash, Salt = x.Salt, IsActive = x.IsActive, CreatedAt = x.CreatedAt, DeactivatedAt = x.DeactivatedAt }));
            foreach (var cluster in envelope.Data.Clusters)
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
                    BackupRestoreMaxDop = cluster.BackupRestoreMaxDop,
                    ClickHouseClusterName = cluster.ClickHouseClusterName,
                    IsDeleted = cluster.IsDeleted,
                    CreatedAt = cluster.CreatedAt,
                    UpdatedAt = cluster.UpdatedAt,
                    DeletedAt = cluster.DeletedAt,
                    AccessNodes = cluster.AccessNodes.Select(n => new ClickHouseAccessNodeEntity { Id = n.Id, Host = n.Host, Port = n.Port, UseTls = n.UseTls }).ToList()
                });
            }
            db.BackupTargets.AddRange(envelope.Data.BackupTargets.Select(x => new BackupTargetEntity { Id = x.Id, Name = x.Name, Type = x.Type, Endpoint = x.S3.Endpoint, Region = x.S3.Region, Bucket = x.S3.Bucket, PathPrefix = x.S3.PathPrefix, ForcePathStyle = x.S3.ForcePathStyle, EncryptedAccessKey = null, EncryptedAccessKeyKeyId = null, EncryptedSecretKey = null, EncryptedSecretKeyKeyId = null, IsDeleted = x.IsDeleted, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, DeletedAt = x.DeletedAt }));
            db.BackupPolicies.AddRange(envelope.Data.BackupPolicies.Select(x => new BackupPolicyEntity
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
                IsSystemDefault = x.IsSystemDefault,
                IsDeleted = x.IsDeleted,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt,
                DeletedAt = x.DeletedAt
            }));
            db.BackupSchedules.AddRange(envelope.Data.BackupSchedules.Select(x => new BackupScheduleEntity { Id = x.Id, Name = x.Name, PolicyId = x.PolicyId, BackupType = x.BackupType, CronExpression = x.CronExpression, TimeZoneId = x.TimeZoneId, IsEnabled = x.IsEnabled, MissedRunGracePeriod = x.MissedRunGracePeriod, Description = x.Description, IsSystemDefault = x.IsSystemDefault, IsDeleted = x.IsDeleted, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, DeletedAt = x.DeletedAt }));

            if (!configOnly)
            {
                db.SchemaDefinitions.AddRange(envelope.Data.SchemaDefinitions.Select(x => new SchemaDefinitionEntity { Id = x.Id, SchemaHash = x.SchemaHash, Database = x.Database, Table = x.Table, Engine = x.Engine, CreateTableSql = x.CreateTableSql, ColumnsJson = x.ColumnsJson, CreatedAt = x.CreatedAt }));
                db.Backups.AddRange(envelope.Data.Backups.Select(x => new BackupEntity { Id = x.Id, TriggerType = x.TriggerType, Status = NormalizeBackupRunStatus(x.Status), BackupType = x.BackupType, ContentMode = x.ContentMode, SourceClusterId = x.SourceClusterId, TargetId = x.TargetId, PolicyId = x.PolicyId, ScheduleId = x.ScheduleId, ManualRequestJson = x.ManualRequestJson, RequestedByUserId = x.RequestedByUserId, RequestedByName = x.RequestedByName, CreatedAt = x.CreatedAt, QueuedAt = x.QueuedAt, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is BackupRunStatus.Queued or BackupRunStatus.Running, x.CompletedAt, importedAt), Error = x.Error, FailureReason = NormalizeFailureReason(x.Status is BackupRunStatus.Queued or BackupRunStatus.Running, x.FailureReason), IsPinned = x.IsPinned, PinnedAt = x.PinnedAt, PinnedByUserId = x.PinnedByUserId, PinnedByName = x.PinnedByName, DeletionReason = x.DeletionReason, DeletionRequestedAt = x.DeletionRequestedAt, DeletionStartedAt = x.DeletionStartedAt, DeletedAt = x.DeletedAt, DeletionError = x.DeletionError, DeletionAttemptCount = x.DeletionAttemptCount }));
                db.BackupTables.AddRange(envelope.Data.BackupTables.Select(x => new BackupTableEntity { Id = x.Id, BackupId = x.BackupId, EffectiveBackupType = x.EffectiveBackupType, ParentFullBackupId = x.ParentFullBackupId, ParentFullBackupTableId = x.ParentFullBackupTableId, Database = x.Database, Table = x.Table, Engine = x.Engine, DataBackedUp = x.DataBackedUp, SchemaDefinitionId = x.SchemaDefinitionId, S3Path = x.S3Path, BackupSizeBytes = x.BackupSizeBytes, Status = NormalizeBackupTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.Error) }));
                db.BackupTableShards.AddRange(envelope.Data.BackupTableShards.Select(x => new BackupTableShardEntity { Id = x.Id, BackupTableId = x.BackupTableId, EffectiveBackupType = x.EffectiveBackupType, ParentFullBackupId = x.ParentFullBackupId, ParentFullBackupTableShardId = x.ParentFullBackupTableShardId, SourceShardNumber = x.SourceShardNumber, SourceShardName = x.SourceShardName, ReplicaNumber = x.ReplicaNumber, Host = x.Host, Port = x.Port, UseTls = x.UseTls, S3Path = x.S3Path, BackupSizeBytes = x.BackupSizeBytes, Status = NormalizeBackupTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is BackupTableStatus.Queued or BackupTableStatus.Running, x.Error) }));
                db.Restores.AddRange(envelope.Data.Restores.Select(x => new RestoreEntity { Id = x.Id, BackupId = x.BackupId, TargetClusterId = x.TargetClusterId, Status = NormalizeRestoreRunStatus(x.Status), Append = x.Append, AllowSchemaMismatch = x.AllowSchemaMismatch, Layout = x.Layout, SourceShard = x.SourceShard, TargetShard = x.TargetShard, RequestJson = x.RequestJson, RequestedByUserId = x.RequestedByUserId, RequestedByName = x.RequestedByName, CreatedAt = x.CreatedAt, QueuedAt = x.QueuedAt, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running, x.CompletedAt, importedAt), Error = x.Error, FailureReason = NormalizeFailureReason(x.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running, x.FailureReason) }));
                db.RestoreTables.AddRange(envelope.Data.RestoreTables.Select(x => new RestoreTableEntity { Id = x.Id, RestoreId = x.RestoreId, BackupTableId = x.BackupTableId, SourceDatabase = x.SourceDatabase, SourceTable = x.SourceTable, TargetDatabase = x.TargetDatabase, TargetTable = x.TargetTable, Append = x.Append, AllowSchemaMismatch = x.AllowSchemaMismatch, SchemaOnly = x.SchemaOnly, Status = NormalizeRestoreTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, Warning = x.Warning, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.Error) }));
                db.RestoreTableShards.AddRange(envelope.Data.RestoreTableShards.Select(x => new RestoreTableShardEntity { Id = x.Id, RestoreTableId = x.RestoreTableId, BackupTableShardId = x.BackupTableShardId, SourceShardNumber = x.SourceShardNumber, TargetShardNumber = x.TargetShardNumber, TargetShardName = x.TargetShardName, TargetReplicaNumber = x.TargetReplicaNumber, TargetHost = x.TargetHost, TargetPort = x.TargetPort, TargetUseTls = x.TargetUseTls, LayoutRole = x.LayoutRole, RestoreDatabase = x.RestoreDatabase, RestoreTableName = x.RestoreTableName, Status = NormalizeRestoreTableStatus(x.Status), ClickHouseOperationId = x.ClickHouseOperationId, ClickHouseStatus = x.ClickHouseStatus, Warning = x.Warning, StartedAt = x.StartedAt, CompletedAt = NormalizeCompletedAt(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.CompletedAt, importedAt), Error = NormalizeError(x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running, x.Error) }));
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
                    importedBackups = configOnly ? 0 : envelope.Data.Backups.Count,
                    importedRestores = configOnly ? 0 : envelope.Data.Restores.Count
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
    private static void ValidateImportPayload(ExportEnvelope envelope, bool configOnly)
    {
        var data = envelope.Data;
        if (configOnly && HasOperationalRows(data))
        {
            throw new InvalidOperationException("Config import payload must not include backup or restore operational state.");
        }

        EnsureUnique(data.Users.Select(x => x.Id), "users");
        EnsureUnique(data.AccessTokens.Select(x => x.Id), "access tokens");
        EnsureUnique(data.Clusters.Select(x => x.Id), "clusters");
        EnsureUnique(data.BackupTargets.Select(x => x.Id), "backup targets");
        EnsureUnique(data.BackupPolicies.Select(x => x.Id), "backup policies");
        EnsureUnique(data.BackupSchedules.Select(x => x.Id), "backup schedules");
        EnsureUnique(data.SchemaDefinitions.Select(x => x.Id), "schema definitions");
        EnsureUnique(data.Backups.Select(x => x.Id), "backup runs");
        EnsureUnique(data.BackupTables.Select(x => x.Id), "backup tables");
        EnsureUnique(data.BackupTableShards.Select(x => x.Id), "backup table shards");
        EnsureUnique(data.Restores.Select(x => x.Id), "restore runs");
        EnsureUnique(data.RestoreTables.Select(x => x.Id), "restore tables");
        EnsureUnique(data.RestoreTableShards.Select(x => x.Id), "restore table shards");

        var userIds = data.Users.Select(x => x.Id).ToHashSet();
        var clusterIds = data.Clusters.Select(x => x.Id).ToHashSet();
        var targetIds = data.BackupTargets.Select(x => x.Id).ToHashSet();
        var policyIds = data.BackupPolicies.Select(x => x.Id).ToHashSet();
        var scheduleIds = data.BackupSchedules.Select(x => x.Id).ToHashSet();
        var schemaDefinitionIds = data.SchemaDefinitions.Select(x => x.Id).ToHashSet();
        var backupIds = data.Backups.Select(x => x.Id).ToHashSet();
        var backupTableIds = data.BackupTables.Select(x => x.Id).ToHashSet();
        var backupTableShardIds = data.BackupTableShards.Select(x => x.Id).ToHashSet();
        var restoreIds = data.Restores.Select(x => x.Id).ToHashSet();
        var restoreTableIds = data.RestoreTables.Select(x => x.Id).ToHashSet();

        foreach (var token in data.AccessTokens)
        {
            EnsureReference(userIds, token.UserId, $"access token {token.Id} user");
        }

        foreach (var policy in data.BackupPolicies)
        {
            EnsureReference(clusterIds, policy.SourceClusterId, $"backup policy {policy.Id} source cluster");
            EnsureOptionalReference(targetIds, policy.TargetId, $"backup policy {policy.Id} target");
            if (policy.ContentMode == BackupContentMode.SchemaAndData && policy.TargetId is null)
            {
                throw new InvalidOperationException($"backup policy {policy.Id} target is required for schema+data policies.");
            }
        }

        foreach (var schedule in data.BackupSchedules)
        {
            EnsureReference(policyIds, schedule.PolicyId, $"backup schedule {schedule.Id} policy");
        }

        foreach (var backup in data.Backups)
        {
            EnsureReference(clusterIds, backup.SourceClusterId, $"backup run {backup.Id} source cluster");
            EnsureOptionalReference(targetIds, backup.TargetId, $"backup run {backup.Id} target");
            if (backup.ContentMode == BackupContentMode.SchemaAndData && backup.TargetId is null)
            {
                throw new InvalidOperationException($"backup run {backup.Id} target is required for schema+data backups.");
            }
            EnsureOptionalReference(policyIds, backup.PolicyId, $"backup run {backup.Id} policy");
            EnsureOptionalReference(scheduleIds, backup.ScheduleId, $"backup run {backup.Id} schedule");
            EnsureOptionalReference(userIds, backup.RequestedByUserId, $"backup run {backup.Id} requested user");
            EnsureOptionalReference(userIds, backup.PinnedByUserId, $"backup run {backup.Id} pinned user");
        }

        foreach (var table in data.BackupTables)
        {
            EnsureReference(backupIds, table.BackupId, $"backup table {table.Id} backup run");
            EnsureOptionalReference(schemaDefinitionIds, table.SchemaDefinitionId, $"backup table {table.Id} schema definition");
            EnsureOptionalReference(backupIds, table.ParentFullBackupId, $"backup table {table.Id} parent full backup");
            EnsureOptionalReference(backupTableIds, table.ParentFullBackupTableId, $"backup table {table.Id} parent full backup table");
        }

        foreach (var shard in data.BackupTableShards)
        {
            EnsureReference(backupTableIds, shard.BackupTableId, $"backup table shard {shard.Id} backup table");
            EnsureOptionalReference(backupIds, shard.ParentFullBackupId, $"backup table shard {shard.Id} parent full backup");
            EnsureOptionalReference(backupTableShardIds, shard.ParentFullBackupTableShardId, $"backup table shard {shard.Id} parent full backup shard");
        }

        foreach (var restore in data.Restores)
        {
            EnsureReference(backupIds, restore.BackupId, $"restore run {restore.Id} backup");
            EnsureReference(clusterIds, restore.TargetClusterId, $"restore run {restore.Id} target cluster");
            EnsureOptionalReference(userIds, restore.RequestedByUserId, $"restore run {restore.Id} requested user");
        }

        foreach (var table in data.RestoreTables)
        {
            EnsureReference(restoreIds, table.RestoreId, $"restore table {table.Id} restore run");
            EnsureReference(backupTableIds, table.BackupTableId, $"restore table {table.Id} backup table");
        }

        foreach (var shard in data.RestoreTableShards)
        {
            EnsureReference(restoreTableIds, shard.RestoreTableId, $"restore table shard {shard.Id} restore table");
            EnsureReference(backupTableShardIds, shard.BackupTableShardId, $"restore table shard {shard.Id} backup table shard");
        }
    }

    private static bool HasOperationalRows(ExportPayload data) =>
        data.SchemaDefinitions.Count > 0 ||
        data.Backups.Count > 0 ||
        data.BackupTables.Count > 0 ||
        data.BackupTableShards.Count > 0 ||
        data.Restores.Count > 0 ||
        data.RestoreTables.Count > 0 ||
        data.RestoreTableShards.Count > 0;

    private static void EnsureUnique(IEnumerable<Guid> ids, string collectionName)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Import payload contains duplicate {collectionName} id {id}.");
            }
        }
    }

    private static void EnsureReference(HashSet<Guid> ids, Guid id, string relationship)
    {
        if (!ids.Contains(id))
        {
            throw new InvalidOperationException($"Import payload references missing {relationship} id {id}.");
        }
    }

    private static void EnsureOptionalReference(HashSet<Guid> ids, Guid? id, string relationship)
    {
        if (id is { } value)
        {
            EnsureReference(ids, value, relationship);
        }
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


