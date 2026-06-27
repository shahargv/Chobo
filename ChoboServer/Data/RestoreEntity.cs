using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class RestoreEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BackupId { get; set; }
    public BackupEntity? Backup { get; set; }
    public Guid TargetClusterId { get; set; }
    public ClickHouseClusterEntity? TargetCluster { get; set; }
    public RestoreRunStatus Status { get; set; } = RestoreRunStatus.Queued;
    public bool Append { get; set; }
    public bool AllowSchemaMismatch { get; set; }
    public RestoreLayout Layout { get; set; } = RestoreLayout.Preserve;
    public int? SourceShard { get; set; }
    public int? TargetShard { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string ClickHouseRestoreSettingsJson { get; set; } = "{}";
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = "system";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? FailureReason { get; set; }
    public List<RestoreTableEntity> Tables { get; set; } = [];
}
