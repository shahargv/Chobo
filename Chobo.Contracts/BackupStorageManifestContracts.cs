using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chobo.Contracts;
public sealed record BackupStorageManifestV1(
    int ManifestVersion,
    int ApiVersion,
    int SchemaVersion,
    string ProductVersion,
    DateTimeOffset GeneratedAt,
    BackupStorageManifestRunV1 Backup,
    BackupStorageManifestTargetV1 Target,
    BackupStorageManifestClusterV1 SourceCluster,
    BackupStorageManifestPolicyV1? Policy,
    BackupStorageManifestScheduleV1? Schedule,
    IReadOnlyList<string> RequiredStoragePaths,
    IReadOnlyList<BackupStorageManifestSchemaV1> Schemas,
    IReadOnlyList<BackupStorageManifestTableV1> Tables);

public sealed record BackupStorageManifestRunV1(
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
    string? StorageRootPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
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
    int DeletionAttemptCount);

public sealed record BackupStorageManifestTargetV1(
    Guid Id,
    string Name,
    string Type,
    IReadOnlyDictionary<string, JsonElement>? Settings,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] S3TargetSettingsDto? S3 = null);

public sealed record BackupStorageManifestClusterV1(
    Guid Id,
    string Name,
    ClusterMode Mode,
    IReadOnlyList<AccessNodeDto> AccessNodes,
    int? BackupRestoreMaxDop,
    int NodeMaxDopDefault,
    IReadOnlyList<ClusterNodeMaxDopOverrideDto> NodeMaxDopOverrides,
    int ShardMaxDopDefault,
    IReadOnlyList<ClusterShardMaxDopOverrideDto> ShardMaxDopOverrides,
    string? ClickHouseClusterName,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt);

public sealed record BackupStorageManifestPolicyV1(
    Guid Id,
    string Name,
    Guid SourceClusterId,
    Guid? TargetId,
    BackupContentMode ContentMode,
    int SelectorJsonVersion,
    PolicySelector Selector,
    BackupRetentionDto? Retention,
    FailedBackupRetentionMode FailedBackupRetentionMode,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt,
    int? MaxAgeHoursForBaseBackup = null);

public sealed record BackupStorageManifestScheduleV1(
    Guid Id,
    string Name,
    Guid PolicyId,
    BackupType BackupType,
    string CronExpression,
    string TimeZoneId,
    bool IsEnabled,
    TimeSpan? MissedRunGracePeriod,
    string? Description,
    bool IsSystemDefault,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt);

public sealed record BackupStorageManifestSchemaV1(
    Guid Id,
    string SchemaHash,
    string Database,
    string Table,
    string Engine,
    string CreateTableSql,
    string ColumnsJson,
    DateTimeOffset CreatedAt);

public sealed record BackupStorageManifestTableV1(
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
    string? StoragePath,
    long? BackupSizeBytes,
    BackupTableStatus Status,
    string? ClickHouseOperationId,
    string? ClickHouseStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    IReadOnlyList<BackupStorageManifestShardV1> Shards)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? S3Path { get; init; }
}

public sealed record BackupStorageManifestShardV1(
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
    string? StoragePath,
    long? BackupSizeBytes,
    BackupTableStatus Status,
    string? ClickHouseOperationId,
    string? ClickHouseStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? S3Path { get; init; }
}
public sealed record RecoverBackupMetadataFromPathRequest(Guid TargetId, string BackupPath);

public sealed record RecoverBackupMetadataScanRequest(Guid TargetId, string ScanRoot);

public sealed record BackupMetadataRecoveryResult(
    int ScannedManifestCount,
    int ImportedBackupCount,
    int UpdatedBackupCount,
    int SkippedManifestCount,
    IReadOnlyList<BackupMetadataRecoveryItem> Items,
    IReadOnlyList<string> Errors);

public sealed record BackupMetadataRecoveryItem(
    Guid BackupId,
    BackupRunStatus Status,
    string Source,
    bool Imported,
    bool Updated,
    string Message);

public sealed record UpdateClusterCredentialsRequest(string? UserName, string? Password);
