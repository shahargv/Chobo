using Chobo.Contracts;
using ChoboServer.Data;

namespace ChoboServer.Application;

internal static class BackupRestoreMapping
{
    public static BackupDto ToDto(BackupEntity x, int? tableCount = null, long? backupSizeBytes = null, IReadOnlyList<Guid>? relatedFullBackupIds = null, bool includeTables = true) =>
        new(
            x.Id,
            x.TriggerType,
            x.Status,
            x.BackupType,
            x.ContentMode,
            x.SourceClusterId,
            x.TargetId,
            x.PolicyId,
            x.ScheduleId,
            x.RequestedByUserId,
            x.RequestedByName,
            x.ManualRequestJson,
            x.CreatedAt,
            x.StartedAt,
            x.CompletedAt,
            x.Error,
            x.FailureReason,
            x.IsPinned,
            x.PinnedAt,
            x.PinnedByUserId,
            x.PinnedByName,
            x.DeletionReason,
            x.DeletionRequestedAt,
            x.DeletionStartedAt,
            x.DeletedAt,
            x.DeletionError,
            x.DeletionAttemptCount,
            tableCount ?? x.Tables.Count,
            backupSizeBytes ?? CalculateBackupSizeBytes(x.Tables),
            relatedFullBackupIds ?? CalculateRelatedFullBackupIds(x.Tables),
            includeTables ? x.Tables.OrderBy(t => t.Database).ThenBy(t => t.Table).Select(ToDto).ToList() : []);


    public static BackupDto ToSummaryDto(
        Guid id,
        BackupTriggerType triggerType,
        BackupRunStatus status,
        BackupType backupType,
        BackupContentMode contentMode,
        Guid sourceClusterId,
        Guid? targetId,
        Guid? policyId,
        Guid? scheduleId,
        Guid? requestedByUserId,
        string requestedByName,
        string? manualRequestJson,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? error,
        string? failureReason,
        bool isPinned,
        DateTimeOffset? pinnedAt,
        Guid? pinnedByUserId,
        string? pinnedByName,
        string? deletionReason,
        DateTimeOffset? deletionRequestedAt,
        DateTimeOffset? deletionStartedAt,
        DateTimeOffset? deletedAt,
        string? deletionError,
        int deletionAttemptCount,
        int tableCount,
        long? backupSizeBytes,
        IReadOnlyList<Guid> relatedFullBackupIds) =>
        new(
            id,
            triggerType,
            status,
            backupType,
            contentMode,
            sourceClusterId,
            targetId,
            policyId,
            scheduleId,
            requestedByUserId,
            requestedByName,
            manualRequestJson,
            createdAt,
            startedAt,
            completedAt,
            error,
            failureReason,
            isPinned,
            pinnedAt,
            pinnedByUserId,
            pinnedByName,
            deletionReason,
            deletionRequestedAt,
            deletionStartedAt,
            deletedAt,
            deletionError,
            deletionAttemptCount,
            tableCount,
            backupSizeBytes,
            relatedFullBackupIds,
            []);
    private static IReadOnlyList<Guid> CalculateRelatedFullBackupIds(IEnumerable<BackupTableEntity> tables) =>
        tables.Select(x => x.ParentFullBackupId)
            .Concat(tables.SelectMany(x => x.Shards).Select(x => x.ParentFullBackupId))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    private static long? CalculateBackupSizeBytes(IEnumerable<BackupTableEntity> tables)
    {
        var sizes = tables.Select(x => x.BackupSizeBytes).Where(x => x.HasValue).ToList();
        return sizes.Count == 0 ? null : sizes.Sum(x => x!.Value);
    }

    public static BackupTableDto ToDto(BackupTableEntity x) =>
        new(
            x.Id,
            x.BackupId,
            x.EffectiveBackupType,
            x.ParentFullBackupId,
            x.ParentFullBackupTableId,
            x.Database,
            x.Table,
            x.Engine,
            x.DataBackedUp,
            x.SchemaDefinitionId,
            x.S3Path,
            x.BackupSizeBytes,
            x.Status,
            x.ClickHouseOperationId,
            x.ClickHouseStatus,
            x.StartedAt,
            x.CompletedAt,
            x.Error,
            x.Shards.OrderBy(s => s.SourceShardNumber).ThenBy(s => s.ReplicaNumber).Select(ToDto).ToList());

    public static BackupTableShardDto ToDto(BackupTableShardEntity x) =>
        new(
            x.Id,
            x.BackupTableId,
            x.EffectiveBackupType,
            x.ParentFullBackupId,
            x.ParentFullBackupTableShardId,
            x.SourceShardNumber,
            x.SourceShardName,
            x.ReplicaNumber,
            x.Host,
            x.Port,
            x.UseTls,
            x.S3Path,
            x.BackupSizeBytes,
            x.Status,
            x.ClickHouseOperationId,
            x.ClickHouseStatus,
            x.StartedAt,
            x.CompletedAt,
            x.Error);

    public static RestoreDto ToDto(RestoreEntity x) =>
        new(
            x.Id,
            x.BackupId,
            x.TargetClusterId,
            x.Status,
            x.Append,
            x.AllowSchemaMismatch,
            x.Layout,
            x.SourceShard,
            x.TargetShard,
            x.RequestedByUserId,
            x.RequestedByName,
            x.RequestJson,
            x.CreatedAt,
            x.StartedAt,
            x.CompletedAt,
            x.Error,
            x.FailureReason,
            x.Tables.OrderBy(t => t.TargetDatabase).ThenBy(t => t.TargetTable).Select(ToDto).ToList());

    public static RestoreTableDto ToDto(RestoreTableEntity x) =>
        new(
            x.Id,
            x.RestoreId,
            x.BackupTableId,
            x.SourceDatabase,
            x.SourceTable,
            x.TargetDatabase,
            x.TargetTable,
            x.Append,
            x.AllowSchemaMismatch,
            x.SchemaOnly,
            x.Status,
            x.ClickHouseOperationId,
            x.ClickHouseStatus,
            x.Warning,
            x.StartedAt,
            x.CompletedAt,
            x.Error,
            x.Shards.OrderBy(s => s.SourceShardNumber).ThenBy(s => s.TargetShardNumber).Select(ToDto).ToList());

    public static RestoreTableShardDto ToDto(RestoreTableShardEntity x) =>
        new(
            x.Id,
            x.RestoreTableId,
            x.BackupTableShardId,
            x.SourceShardNumber,
            x.TargetShardNumber,
            x.TargetShardName,
            x.TargetReplicaNumber,
            x.TargetHost,
            x.TargetPort,
            x.TargetUseTls,
            x.LayoutRole,
            x.RestoreDatabase,
            x.RestoreTableName,
            x.Status,
            x.ClickHouseOperationId,
            x.ClickHouseStatus,
            x.Warning,
            x.StartedAt,
            x.CompletedAt,
            x.Error);
}
