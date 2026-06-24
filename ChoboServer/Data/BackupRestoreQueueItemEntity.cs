using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class BackupRestoreQueueItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BackupRestoreQueueKind Kind { get; set; }
    public long Position { get; set; }
    public bool IsForced { get; set; }
    public DateTimeOffset? ForcedAt { get; set; }
    public Guid? ForcedByUserId { get; set; }
    public string? ForcedByName { get; set; }
    public Guid OperationId { get; set; }
    public Guid TableId { get; set; }
    public Guid ShardId { get; set; }
    public Guid ClusterId { get; set; }
    public int LogicalShardNumber { get; set; }
    public string? LogicalShardName { get; set; }
    public string? NodeHost { get; set; }
    public int? NodePort { get; set; }
    public bool? NodeUseTls { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}