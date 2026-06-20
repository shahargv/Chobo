namespace Chobo.Contracts;

public enum BackupType { Full, Incremental }

public sealed record BackupScheduleDto(Guid Id, string Name, Guid PolicyId, BackupType BackupType, string CronExpression, string TimeZoneId, bool IsEnabled, TimeSpan? MissedRunGracePeriod, string? Description, bool IsSystemDefault, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record UpsertScheduleRequest(string Name, Guid PolicyId, BackupType BackupType, string CronExpression, string TimeZoneId, bool IsEnabled, TimeSpan? MissedRunGracePeriod, string? Description);

public sealed record ValidateScheduleCronRequest(string CronExpression, string TimeZoneId);

public sealed record ValidateScheduleCronResponse(bool IsValid, string? Error, IReadOnlyList<DateTimeOffset> NextRuns);

