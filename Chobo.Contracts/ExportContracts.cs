using System.Text.Json;

namespace Chobo.Contracts;

public sealed record ExportEnvelope(
    int ExportVersion,
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string ProductVersion,
    ExportPayload Data);

public sealed record ExportPayload(
    IReadOnlyList<UserExport> Users,
    IReadOnlyList<AccessTokenExport> AccessTokens,
    IReadOnlyList<ClusterExport> Clusters,
    IReadOnlyList<BackupTargetExport> BackupTargets,
    IReadOnlyList<BackupPolicyExport> BackupPolicies,
    IReadOnlyList<BackupScheduleExport> BackupSchedules,
    IReadOnlyList<SchemaDefinitionExport> SchemaDefinitions,
    IReadOnlyList<BackupExport> Backups,
    IReadOnlyList<BackupTableExport> BackupTables,
    IReadOnlyList<BackupTableShardExport> BackupTableShards,
    IReadOnlyList<RestoreExport> Restores,
    IReadOnlyList<RestoreTableExport> RestoreTables,
    IReadOnlyList<RestoreTableShardExport> RestoreTableShards);

public sealed record UserExport(Guid Id, string UserName, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record AccessTokenExport(Guid Id, Guid UserId, string Name, string TokenHash, string TokenLookupHash, string Salt, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record ClusterExport(Guid Id, string Name, ClusterMode Mode, string? ClickHouseClusterName, IReadOnlyList<AccessNodeDto> AccessNodes, string? EncryptedUserName, Guid? EncryptedUserNameKeyId, string? EncryptedPassword, Guid? EncryptedPasswordKeyId, int? BackupRestoreMaxDop, int NodeMaxDopDefault, IReadOnlyList<ClusterNodeMaxDopOverrideDto> NodeMaxDopOverrides, int ShardMaxDopDefault, IReadOnlyList<ClusterShardMaxDopOverrideDto> ShardMaxDopOverrides, IReadOnlyDictionary<string, JsonElement>? ClickHouseBackupSettings, IReadOnlyDictionary<string, JsonElement>? ClickHouseRestoreSettings, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record BackupTargetExport(Guid Id, string Name, BackupTargetType Type, S3TargetSettingsDto S3, string? EncryptedAccessKey, Guid? EncryptedAccessKeyKeyId, string? EncryptedSecretKey, Guid? EncryptedSecretKeyKeyId, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record BackupPolicyExport(Guid Id, string Name, Guid SourceClusterId, Guid? TargetId, BackupContentMode ContentMode, int SelectorJsonVersion, PolicySelector Selector, BackupRetentionDto? Retention, FailedBackupRetentionMode FailedBackupRetentionMode, IReadOnlyDictionary<string, JsonElement>? ClickHouseBackupSettings, IReadOnlyDictionary<string, JsonElement>? ClickHouseRestoreSettings, bool IsSystemDefault, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record BackupScheduleExport(Guid Id, string Name, Guid PolicyId, BackupType BackupType, string CronExpression, string TimeZoneId, bool IsEnabled, TimeSpan? MissedRunGracePeriod, string? Description, bool IsSystemDefault, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record SchemaDefinitionExport(Guid Id, string SchemaHash, string Database, string Table, string Engine, string CreateTableSql, string ColumnsJson, DateTimeOffset CreatedAt);

public sealed record BackupExport(Guid Id, BackupTriggerType TriggerType, BackupRunStatus Status, BackupType BackupType, BackupContentMode ContentMode, Guid SourceClusterId, Guid? TargetId, Guid? PolicyId, Guid? ScheduleId, string? ManualRequestJson, Guid? RequestedByUserId, string RequestedByName, DateTimeOffset CreatedAt, DateTimeOffset? QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error, string? FailureReason, bool IsPinned, DateTimeOffset? PinnedAt, Guid? PinnedByUserId, string? PinnedByName, string? DeletionReason, DateTimeOffset? DeletionRequestedAt, DateTimeOffset? DeletionStartedAt, DateTimeOffset? DeletedAt, string? DeletionError, int DeletionAttemptCount, IReadOnlyDictionary<string, JsonElement>? ClickHouseBackupSettings);

public sealed record BackupTableExport(Guid Id, Guid BackupId, BackupType EffectiveBackupType, Guid? ParentFullBackupId, Guid? ParentFullBackupTableId, string Database, string Table, string Engine, bool DataBackedUp, Guid? SchemaDefinitionId, string S3Path, long? BackupSizeBytes, BackupTableStatus Status, string? ClickHouseOperationId, string? ClickHouseStatus, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error);

public sealed record BackupTableShardExport(Guid Id, Guid BackupTableId, BackupType EffectiveBackupType, Guid? ParentFullBackupId, Guid? ParentFullBackupTableShardId, int SourceShardNumber, string? SourceShardName, int ReplicaNumber, string Host, int Port, bool UseTls, string S3Path, long? BackupSizeBytes, BackupTableStatus Status, string? ClickHouseOperationId, string? ClickHouseStatus, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error);

public sealed record RestoreExport(Guid Id, Guid BackupId, Guid TargetClusterId, RestoreRunStatus Status, bool Append, bool AllowSchemaMismatch, RestoreLayout Layout, int? SourceShard, int? TargetShard, string RequestJson, Guid? RequestedByUserId, string RequestedByName, DateTimeOffset CreatedAt, DateTimeOffset? QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error, string? FailureReason, IReadOnlyDictionary<string, JsonElement>? ClickHouseRestoreSettings);

public sealed record RestoreTableExport(Guid Id, Guid RestoreId, Guid BackupTableId, string SourceDatabase, string SourceTable, string TargetDatabase, string TargetTable, bool Append, bool AllowSchemaMismatch, bool SchemaOnly, RestoreTableStatus Status, string? ClickHouseOperationId, string? ClickHouseStatus, string? Warning, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error);

public sealed record RestoreTableShardExport(Guid Id, Guid RestoreTableId, Guid BackupTableShardId, int SourceShardNumber, int? TargetShardNumber, string? TargetShardName, int? TargetReplicaNumber, string TargetHost, int TargetPort, bool TargetUseTls, string LayoutRole, string RestoreDatabase, string RestoreTableName, RestoreTableStatus Status, string? ClickHouseOperationId, string? ClickHouseStatus, string? Warning, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error);
