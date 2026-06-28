namespace ChoboServer.Data;

public sealed class BackupTargetEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Type { get; set; } = Chobo.Contracts.StorageProviderTypes.S3;
    public string SettingsJson { get; set; } = "{}";
    public string SecretsJson { get; set; } = "{}";
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
