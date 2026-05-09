using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class ClickHouseClusterEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ClusterMode Mode { get; set; }
    public List<ClickHouseAccessNodeEntity> AccessNodes { get; set; } = [];
    public string? EncryptedUserName { get; set; }
    public string? EncryptedPassword { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

