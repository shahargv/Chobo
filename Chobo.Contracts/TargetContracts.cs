namespace Chobo.Contracts;

public enum BackupTargetType { S3 }

public sealed record BackupTargetDto(Guid Id, string Name, BackupTargetType Type, S3TargetSettingsDto S3, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record S3TargetSettingsDto(string Endpoint, string Region, string Bucket, string? PathPrefix, bool ForcePathStyle);

public sealed record UpsertS3TargetRequest(string Name, string Endpoint, string Region, string Bucket, string? PathPrefix, bool ForcePathStyle, string? AccessKey, string? SecretKey);
