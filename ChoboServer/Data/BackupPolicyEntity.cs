using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class BackupPolicyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid SourceClusterId { get; set; }
    public ClickHouseClusterEntity? SourceCluster { get; set; }
    public Guid? TargetId { get; set; }
    public BackupTargetEntity? Target { get; set; }
    public BackupContentMode ContentMode { get; set; } = BackupContentMode.SchemaAndData;
    public int SelectorJsonVersion { get; set; } = 1;
    public string SelectorJson { get; set; } = "";
    public int? FullRetentionMinutes { get; set; }
    public int? IncrementalRetentionMinutes { get; set; }
    public int MinBackupsToKeep { get; set; }
    public int MinFullBackupsToKeep { get; set; }
    public FailedBackupRetentionMode FailedBackupRetentionMode { get; set; } = FailedBackupRetentionMode.KeepAndExcludeFromMinBackupsToKeep;
    public bool IsSystemDefault { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}



