namespace ChoboServer.Data;

public sealed class AuditEntryEntity
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? OperationId { get; set; }
    public string Details { get; set; } = "";
}


