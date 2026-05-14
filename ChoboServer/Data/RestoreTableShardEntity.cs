using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class RestoreTableShardEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RestoreTableId { get; set; }
    public RestoreTableEntity? RestoreTable { get; set; }
    public Guid BackupTableShardId { get; set; }
    public BackupTableShardEntity? BackupTableShard { get; set; }
    public int SourceShardNumber { get; set; }
    public int? TargetShardNumber { get; set; }
    public string? TargetShardName { get; set; }
    public int? TargetReplicaNumber { get; set; }
    public string TargetHost { get; set; } = "";
    public int TargetPort { get; set; }
    public bool TargetUseTls { get; set; }
    public string LayoutRole { get; set; } = "";
    public string RestoreDatabase { get; set; } = "";
    public string RestoreTableName { get; set; } = "";
    public RestoreTableStatus Status { get; set; } = RestoreTableStatus.Queued;
    public string? ClickHouseOperationId { get; set; }
    public string? ClickHouseStatus { get; set; }
    public string? Warning { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
