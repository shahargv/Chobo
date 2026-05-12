namespace Chobo.Contracts;

public sealed record DashboardDto(
    DateTimeOffset GeneratedAt,
    int FutureWindowHours,
    IReadOnlyList<DashboardRunningBackupDto> RunningBackups,
    IReadOnlyList<DashboardScheduleDto> Schedules,
    IReadOnlyList<DashboardFutureScheduleDto> FutureSchedules);

public sealed record DashboardRunningBackupDto(
    Guid BackupId,
    BackupRunStatus Status,
    BackupTriggerType TriggerType,
    Guid? PolicyId,
    string? PolicyName,
    Guid? ScheduleId,
    string? ScheduleName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    int TableCount,
    int ShardCount,
    int SucceededShardCount,
    int FailedShardCount,
    int RunningShardCount);

public sealed record DashboardScheduleDto(
    Guid ScheduleId,
    string ScheduleName,
    Guid PolicyId,
    string? PolicyName,
    BackupType BackupType,
    string CronExpression,
    string TimeZoneId,
    bool IsEnabled,
    TimeSpan? MissedRunGracePeriod,
    DateTimeOffset? LastRunAt,
    BackupRunStatus? LastRunStatus,
    DateTimeOffset? LastSuccessfulRunCompletedAt);

public sealed record DashboardFutureScheduleDto(
    Guid ScheduleId,
    string ScheduleName,
    Guid PolicyId,
    string? PolicyName,
    BackupType BackupType,
    DateTimeOffset PlannedRunAt,
    string TimeZoneId);
