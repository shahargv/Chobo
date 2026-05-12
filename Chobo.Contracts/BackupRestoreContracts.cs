namespace Chobo.Contracts;

public enum BackupRunStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Canceled }

public enum BackupTableStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Skipped }

public enum BackupTriggerType { Manual, Scheduled }

public enum RestoreRunStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Canceled }

public enum RestoreTableStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Skipped }

public enum RestoreLayout { Preserve, SingleNode, Redistribute }

public sealed record BackupDto(
    Guid Id,
    BackupTriggerType TriggerType,
    BackupRunStatus Status,
    Guid SourceClusterId,
    Guid TargetId,
    Guid? PolicyId,
    Guid? ScheduleId,
    Guid? RequestedByUserId,
    string RequestedByName,
    string? ManualRequestJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? FailureReason,
    IReadOnlyList<BackupTableDto> Tables);

public sealed record BackupTableDto(
    Guid Id,
    Guid BackupId,
    string Database,
    string Table,
    string Engine,
    bool DataBackedUp,
    Guid SchemaDefinitionId,
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

public sealed record ManualBackupRequest(Guid ClusterId, Guid TargetId, PolicySelector Selector);

public sealed record BackupListRequest(Guid? PolicyId, string? ClusterName, string? TableName, BackupRunStatus? Status);

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
    DateTimeOffset? CompletedAt,
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
    int? TargetShard = null);
