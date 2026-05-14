using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class BackupTableShardEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BackupTableId { get; set; }
    public BackupTableEntity? BackupTable { get; set; }
    public int SourceShardNumber { get; set; }
    public string? SourceShardName { get; set; }
    public int ReplicaNumber { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool UseTls { get; set; }
    public string S3Path { get; set; } = "";
    public BackupTableStatus Status { get; set; } = BackupTableStatus.Queued;
    public string? ClickHouseOperationId { get; set; }
    public string? ClickHouseStatus { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
