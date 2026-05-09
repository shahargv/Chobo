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
    IReadOnlyList<AuditEntryDto> Audits,
    IReadOnlyList<LogEntryDto> Logs);

public sealed record UserExport(Guid Id, string UserName, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record AccessTokenExport(Guid Id, Guid UserId, string Name, string TokenHash, string TokenLookupHash, string Salt, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record ClusterExport(Guid Id, string Name, ClusterMode Mode, IReadOnlyList<AccessNodeDto> AccessNodes, string? EncryptedUserName, string? EncryptedPassword, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record BackupTargetExport(Guid Id, string Name, BackupTargetType Type, S3TargetSettingsDto S3, string? EncryptedAccessKey, string? EncryptedSecretKey, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record BackupPolicyExport(Guid Id, string Name, Guid SourceClusterId, int SelectorJsonVersion, PolicySelector Selector, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record BackupScheduleExport(Guid Id, string Name, Guid PolicyId, BackupType BackupType, string CronExpression, string TimeZoneId, bool IsEnabled, string? Description, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt);
