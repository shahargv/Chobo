namespace Chobo.Contracts;

public sealed record DashboardDto(
    DateTimeOffset GeneratedAt,
    int FutureWindowHours,
    QueueHealthDto Queue,
    IReadOnlyList<DashboardRunningBackupDto> RunningBackups,
    IReadOnlyList<DashboardScheduleDto> Schedules,
    IReadOnlyList<DashboardFutureScheduleDto> FutureSchedules);

public sealed record QueueHealthDto(int ActiveCount, DateTimeOffset? OldestActiveQueuedAt, double? OldestActiveAgeSeconds);

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
    string? FailureReason,
    bool IsPinned,
    DateTimeOffset? DeletionRequestedAt,
    string? DeletionReason,
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
    string? LastRunFailureReason,
    bool LastRunIsPinned,
    DateTimeOffset? LastRunDeletionRequestedAt,
    DateTimeOffset? LastSuccessfulRunCompletedAt);

public sealed record DashboardFutureScheduleDto(
    Guid ScheduleId,
    string ScheduleName,
    Guid PolicyId,
    string? PolicyName,
    BackupType BackupType,
    DateTimeOffset PlannedRunAt,
    string TimeZoneId);


