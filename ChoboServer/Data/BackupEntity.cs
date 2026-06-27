using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class BackupEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BackupTriggerType TriggerType { get; set; }
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Queued;
    public BackupType BackupType { get; set; } = BackupType.Full;
    public BackupContentMode ContentMode { get; set; } = BackupContentMode.SchemaAndData;
    public Guid SourceClusterId { get; set; }
    public ClickHouseClusterEntity? SourceCluster { get; set; }
    public Guid? TargetId { get; set; }
    public BackupTargetEntity? Target { get; set; }
    public Guid? PolicyId { get; set; }
    public BackupPolicyEntity? Policy { get; set; }
    public Guid? ScheduleId { get; set; }
    public BackupScheduleEntity? Schedule { get; set; }
    public string? ManualRequestJson { get; set; }
    public string ClickHouseBackupSettingsJson { get; set; } = "{}";
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = "system";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? FailureReason { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset? PinnedAt { get; set; }
    public Guid? PinnedByUserId { get; set; }
    public string? PinnedByName { get; set; }
    public string? DeletionReason { get; set; }
    public DateTimeOffset? DeletionRequestedAt { get; set; }
    public DateTimeOffset? DeletionStartedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletionError { get; set; }
    public int DeletionAttemptCount { get; set; }
    public List<BackupTableEntity> Tables { get; set; } = [];
}
