using System.Text.Json;

namespace Chobo.Contracts;

public static class StorageProviderTypes
{
    public const string S3 = "s3";
}

public sealed record BackupTargetDto(
    Guid Id,
    string Name,
    string Type,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<string> SecretFields,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record UpsertBackupTargetRequest(
    string Name,
    string Type,
    IReadOnlyDictionary<string, JsonElement>? Settings,
    IReadOnlyDictionary<string, JsonElement>? Secrets = null,
    bool UpdateSecrets = true);

public sealed record S3TargetSettingsDto(string Endpoint, string Region, string Bucket, string? PathPrefix, bool ForcePathStyle);

public sealed record S3TargetSecretsDto(string? AccessKey, string? SecretKey);

public sealed record UpsertS3TargetRequest(string Name, string Endpoint, string Region, string Bucket, string? PathPrefix, bool ForcePathStyle, string? AccessKey, string? SecretKey);
