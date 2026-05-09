namespace ChoboServer.Data;

public sealed class ClickHouseAccessNodeEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public ClickHouseClusterEntity? Cluster { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9000;
    public bool UseTls { get; set; }
}

