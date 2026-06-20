using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class RestoreTableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RestoreId { get; set; }
    public RestoreEntity? Restore { get; set; }
    public Guid BackupTableId { get; set; }
    public BackupTableEntity? BackupTable { get; set; }
    public string SourceDatabase { get; set; } = "";
    public string SourceTable { get; set; } = "";
    public string TargetDatabase { get; set; } = "";
    public string TargetTable { get; set; } = "";
    public bool Append { get; set; }
    public bool AllowSchemaMismatch { get; set; }
    public bool SchemaOnly { get; set; }
    public RestoreTableStatus Status { get; set; } = RestoreTableStatus.Queued;
    public string? ClickHouseOperationId { get; set; }
    public string? ClickHouseStatus { get; set; }
    public string? Warning { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public List<RestoreTableShardEntity> Shards { get; set; } = [];
}
