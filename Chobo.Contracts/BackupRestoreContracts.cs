namespace Chobo.Contracts;

public enum BackupRunStatus
{
    Queued,
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Canceled,
    ManualDeleteRequested,
    ManualDeleted,
    FailedBackupDeleteRequested,
    FailedBackupDeletedByGarbageCollector,
    BackupExpiredDeleteStarted,
    BackupExpiredDeleted
}

public enum BackupTableStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Skipped }

public enum BackupTriggerType { Manual, Scheduled }

public enum RestoreRunStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Canceled }

public enum RestoreTableStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Skipped }

public enum RestoreLayout { Preserve, SingleNode, Redistribute }

public sealed record BackupDto(
    Guid Id,
    BackupTriggerType TriggerType,
    BackupRunStatus Status,
    BackupType BackupType,
    BackupContentMode ContentMode,
    Guid SourceClusterId,
    Guid? TargetId,
    Guid? PolicyId,
    Guid? ScheduleId,
    Guid? RequestedByUserId,
    string RequestedByName,
    string? ManualRequestJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Error,
    string? FailureReason,
    bool IsPinned,
    DateTimeOffset? PinnedAt,
    Guid? PinnedByUserId,
    string? PinnedByName,
    string? DeletionReason,
    DateTimeOffset? DeletionRequestedAt,
    DateTimeOffset? DeletionStartedAt,
    DateTimeOffset? DeletedAt,
    string? DeletionError,
    int DeletionAttemptCount,
    IReadOnlyList<BackupTableDto> Tables);

public sealed record BackupTableDto(
    Guid Id,
    Guid BackupId,
    BackupType EffectiveBackupType,
    Guid? ParentFullBackupId,
    Guid? ParentFullBackupTableId,
    string Database,
    string Table,
    string Engine,
    bool DataBackedUp,
    Guid? SchemaDefinitionId,
    string S3Path,
    BackupTableStatus Status,
    string? ClickHouseOperationId,
    string? ClickHouseStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    IReadOnlyList<BackupTableShardDto> Shards);

public sealed record BackupTableShardDto(
    Guid Id,
    Guid BackupTableId,
    BackupType EffectiveBackupType,
    Guid? ParentFullBackupId,
    Guid? ParentFullBackupTableShardId,
    int SourceShardNumber,
    string? SourceShardName,
    int ReplicaNumber,
    string Host,
    int Port,
    bool UseTls,
    string S3Path,
    BackupTableStatus Status,
    string? ClickHouseOperationId,
    string? ClickHouseStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public sealed record ManualBackupRequest(
    Guid ClusterId,
    Guid? TargetId,
    PolicySelector Selector,
    BackupType BackupType = BackupType.Full,
    Guid? PolicyId = null,
    bool SchemaOnly = false);

public sealed record SchemaBackupSummaryDto(Guid Id, BackupRunStatus Status, BackupContentMode ContentMode, BackupType BackupType, Guid SourceClusterId, string SourceClusterName, Guid? PolicyId, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, int TableCount);

public sealed record SchemaBackupDto(Guid BackupId, BackupRunStatus Status, BackupContentMode ContentMode, IReadOnlyList<SchemaDatabaseDto> Databases);

public sealed record SchemaDatabaseDto(string Database, IReadOnlyList<SchemaTableDto> Tables);

public sealed record SchemaTableDto(Guid BackupTableId, string Database, string Table, string Engine, bool DataBackedUp, string CreateTableSql, string ColumnsJson);

public sealed record BackupListRequest(Guid? PolicyId, string? ClusterName, string? TableName, BackupRunStatus? Status);

public sealed record BackupGarbageCollectorStatusDto(
    bool IsRunning,
    string CurrentRunReason,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    string? LastError,
    int LastMarkedCount,
    int LastPendingCleanupCount,
    int LastCleanedCount,
    int LastFailedCount);

public sealed record RestoreDto(
    Guid Id,
    Guid BackupId,
    Guid TargetClusterId,
    RestoreRunStatus Status,
    bool Append,
    bool AllowSchemaMismatch,
    RestoreLayout Layout,
    int? SourceShard,
    int? TargetShard,
    Guid? RequestedByUserId,
    string RequestedByName,
    string RequestJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Error,
    string? FailureReason,
    IReadOnlyList<RestoreTableDto> Tables);

public sealed record RestoreTableDto(
    Guid Id,
    Guid RestoreId,
    Guid BackupTableId,
    string SourceDatabase,
    string SourceTable,
    string TargetDatabase,
    string TargetTable,
    bool Append,
    bool AllowSchemaMismatch,
    bool SchemaOnly,
    RestoreTableStatus Status,
    string? ClickHouseOperationId,
    string? ClickHouseStatus,
    string? Warning,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    IReadOnlyList<RestoreTableShardDto> Shards);

public sealed record RestoreTableShardDto(
    Guid Id,
    Guid RestoreTableId,
    Guid BackupTableShardId,
    int SourceShardNumber,
    int? TargetShardNumber,
    string? TargetShardName,
    int? TargetReplicaNumber,
    string TargetHost,
    int TargetPort,
    bool TargetUseTls,
    string LayoutRole,
    string RestoreDatabase,
    string RestoreTableName,
    RestoreTableStatus Status,
    string? ClickHouseOperationId,
    string? ClickHouseStatus,
    string? Warning,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public sealed record InitiateRestoreRequest(
    Guid BackupId,
    Guid TargetClusterId,
    string? Database,
    string? Table,
    string? TargetDatabase,
    string? TargetTable,
    bool Append,
    bool AllowSchemaMismatch,
    RestoreLayout? Layout = null,
    int? SourceShard = null,
    int? TargetShard = null,
    IReadOnlyList<RestoreTableMappingRequest>? Tables = null,
    bool SchemaOnly = false,
    IReadOnlyList<int>? SourceShards = null,
    IReadOnlyList<int>? TargetShards = null,
    bool ConfirmDestructive = false);

public sealed record RestoreTableMappingRequest(
    Guid BackupTableId,
    string? TargetDatabase,
    string? TargetTable,
    bool? Append = null,
    bool? AllowSchemaMismatch = null,
    bool? SchemaOnly = null);



