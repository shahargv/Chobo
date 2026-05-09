using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class BackupScheduleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid PolicyId { get; set; }
    public BackupPolicyEntity? Policy { get; set; }
    public BackupType BackupType { get; set; }
    public string CronExpression { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsEnabled { get; set; } = true;
    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
