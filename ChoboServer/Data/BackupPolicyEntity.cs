namespace ChoboServer.Data;

public sealed class BackupPolicyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid SourceClusterId { get; set; }
    public ClickHouseClusterEntity? SourceCluster { get; set; }
    public int SelectorJsonVersion { get; set; } = 1;
    public string SelectorJson { get; set; } = "";
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
