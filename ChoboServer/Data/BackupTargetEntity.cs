using Chobo.Contracts;

namespace ChoboServer.Data;

public sealed class BackupTargetEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public BackupTargetType Type { get; set; } = BackupTargetType.S3;
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string? PathPrefix { get; set; }
    public bool ForcePathStyle { get; set; }
    public string? EncryptedAccessKey { get; set; }
    public string? EncryptedSecretKey { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

