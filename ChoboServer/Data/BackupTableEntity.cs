using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class BackupTableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BackupId { get; set; }
    public BackupEntity? Backup { get; set; }
    public BackupType EffectiveBackupType { get; set; } = BackupType.Full;
    public Guid? ParentFullBackupId { get; set; }
    public Guid? ParentFullBackupTableId { get; set; }
    public BackupTableEntity? ParentFullBackupTable { get; set; }
    public string Database { get; set; } = "";
    public string Table { get; set; } = "";
    public string Engine { get; set; } = "";
    public bool DataBackedUp { get; set; }
    public Guid? SchemaDefinitionId { get; set; }
    public SchemaDefinitionEntity? SchemaDefinition { get; set; }
    public string S3Path { get; set; } = "";
    public BackupTableStatus Status { get; set; } = BackupTableStatus.Queued;
    public string? ClickHouseOperationId { get; set; }
    public string? ClickHouseStatus { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public List<BackupTableShardEntity> Shards { get; set; } = [];
}

