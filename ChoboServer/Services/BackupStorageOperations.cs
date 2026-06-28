using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public interface IBackupStorageOperations
{
    Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default);
    Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default);
    Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListObjectPathsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupStorageObjectInfo>> ListObjectsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default);
    Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default);
    Task<StorageConnectionTestResult> TestConnectionAsync(BackupTargetEntity target, CancellationToken cancellationToken = default);
}

public interface IBackupStorageProvider : IBackupStorageOperations
{
    string Type { get; }
    IReadOnlyList<string> SecretFields { get; }
    Task ConfigureNewTargetAsync(BackupTargetEntity target, UpsertBackupTargetRequest request, CancellationToken cancellationToken = default);
    Task ConfigureExistingTargetAsync(BackupTargetEntity target, UpsertBackupTargetRequest request, CancellationToken cancellationToken = default);
    BackupTargetDto ToDto(BackupTargetEntity target);
    Task<ClickHouseStorageDestination> CreateBackupDestinationAsync(BackupTargetEntity target, string storagePath, string? baseBackupStoragePath, CancellationToken cancellationToken = default);
    Task<ClickHouseStorageDestination> CreateRestoreDestinationAsync(BackupTargetEntity target, string storagePath, CancellationToken cancellationToken = default);
}

public interface IBackupStorageProviderRegistry
{
    IBackupStorageProvider Get(string type);
    IBackupStorageProvider Get(BackupTargetEntity target);
    IReadOnlyList<string> Types { get; }
}

public sealed record BackupStorageObjectInfo(string Path, long SizeBytes);

public sealed record ClickHouseStorageDestination(string Expression, IReadOnlyList<(string Name, string Value)> Settings, IReadOnlyList<string> SensitiveValues);

public sealed class BackupStorageProviderRegistry(IEnumerable<IBackupStorageProvider> providers) : IBackupStorageProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IBackupStorageProvider> _providers = providers.ToDictionary(x => x.Type, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Types => _providers.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public IBackupStorageProvider Get(BackupTargetEntity target) => Get(target.Type);

    public IBackupStorageProvider Get(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Backup target type is required.");
        }

        return _providers.TryGetValue(type.Trim(), out var provider)
            ? provider
            : throw new NotSupportedException($"Backup target type '{type}' is not supported.");
    }
}

public sealed class BackupStorageOperations(IBackupStorageProviderRegistry providers) : IBackupStorageOperations
{
    public Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default) =>
        providers.Get(target).DeleteDirectoryAsync(target, directoryPath, cancellationToken);

    public Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default) =>
        providers.Get(target).WriteObjectAsync(target, path, content, cancellationToken);

    public Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
        providers.Get(target).ReadObjectAsync(target, path, cancellationToken);

    public Task<IReadOnlyList<string>> ListObjectPathsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
        providers.Get(target).ListObjectPathsAsync(target, rootPath, cancellationToken);

    public Task<IReadOnlyList<BackupStorageObjectInfo>> ListObjectsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
        providers.Get(target).ListObjectsAsync(target, rootPath, cancellationToken);

    public Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
        providers.Get(target).DeleteObjectAsync(target, path, cancellationToken);

    public Task<StorageConnectionTestResult> TestConnectionAsync(BackupTargetEntity target, CancellationToken cancellationToken = default) =>
        providers.Get(target).TestConnectionAsync(target, cancellationToken);
}

public sealed class S3StorageProvider(
    ICredentialProtector protector,
    IOptionsMonitor<BackupStorageOperationOptions> options,
    IEndpointRewriteService endpointRewrites,
    Serilog.ILogger logger) : IBackupStorageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Serilog.ILogger _logger = logger.ForContext<S3StorageProvider>();

    public string Type => StorageProviderTypes.S3;
    public IReadOnlyList<string> SecretFields { get; } = ["accessKey", "secretKey"];

    public async Task ConfigureNewTargetAsync(BackupTargetEntity target, UpsertBackupTargetRequest request, CancellationToken cancellationToken = default)
    {
        var settings = ReadSettings(request.Settings);
        var secrets = ReadSecrets(request.Secrets);
        ValidateSettings(request.Name, settings);
        ValidateSecrets(secrets, requireCredentials: true);
        target.Type = Type;
        target.SettingsJson = JsonSerializer.Serialize(settings, JsonOptions);
        target.SecretsJson = JsonSerializer.Serialize(await ProtectSecretsAsync(secrets, cancellationToken), JsonOptions);
    }

    public async Task ConfigureExistingTargetAsync(BackupTargetEntity target, UpsertBackupTargetRequest request, CancellationToken cancellationToken = default)
    {
        var settings = ReadSettings(request.Settings);
        ValidateSettings(request.Name, settings);
        target.Type = Type;
        target.SettingsJson = JsonSerializer.Serialize(settings, JsonOptions);
        if (request.UpdateSecrets)
        {
            var secrets = ReadSecrets(request.Secrets);
            ValidateSecrets(secrets, requireCredentials: false);
            target.SecretsJson = JsonSerializer.Serialize(await ProtectSecretsAsync(secrets, ReadStoredSecrets(target.SecretsJson), cancellationToken), JsonOptions);
        }
    }

    public BackupTargetDto ToDto(BackupTargetEntity target) =>
        new(
            target.Id,
            target.Name,
            Type,
            ToDictionary(ReadSettings(target.SettingsJson)),
            SecretFields,
            target.IsDeleted,
            target.CreatedAt,
            target.UpdatedAt);

    public async Task<ClickHouseStorageDestination> CreateBackupDestinationAsync(BackupTargetEntity target, string storagePath, string? baseBackupStoragePath, CancellationToken cancellationToken = default)
    {
        var secrets = await DecryptSecretsAsync(target, cancellationToken);
        var expression = ClickHouseSql.S3(S3Endpoint(target, storagePath), secrets.AccessKey, secrets.SecretKey);
        var settings = string.IsNullOrWhiteSpace(baseBackupStoragePath)
            ? []
            : new[] { ("base_backup", ClickHouseSql.S3(S3Endpoint(target, baseBackupStoragePath), secrets.AccessKey, secrets.SecretKey)) };
        return new ClickHouseStorageDestination(expression, settings, [secrets.AccessKey, secrets.SecretKey]);
    }

    public async Task<ClickHouseStorageDestination> CreateRestoreDestinationAsync(BackupTargetEntity target, string storagePath, CancellationToken cancellationToken = default)
    {
        var secrets = await DecryptSecretsAsync(target, cancellationToken);
        return new ClickHouseStorageDestination(ClickHouseSql.S3(S3Endpoint(target, storagePath), secrets.AccessKey, secrets.SecretKey), [], [secrets.AccessKey, secrets.SecretKey]);
    }

    public async Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default)
    {
        var settings = ReadSettings(target.SettingsJson);
        var prefix = S3TargetUrlBuilder.StoragePath(settings, directoryPath).TrimStart('/');
        var batchSize = Math.Clamp(options.CurrentValue.S3DeleteBatchSize, 1, 1000);
        var deletedCount = 0;
        using var client = await CreateClientAsync(target, cancellationToken);
        while (true)
        {
            var keys = await ListKeyPageAsync(client, settings.Bucket, prefix, batchSize, cancellationToken);
            if (keys.Count == 0)
            {
                _logger.Information("S3 storage directory {DirectoryPath} cleanup completed. DeletedObjectCount={DeletedObjectCount}.", directoryPath, deletedCount);
                return;
            }

            await DeleteStoredObjectsAsync(client, settings.Bucket, keys, cancellationToken);
            deletedCount += keys.Count;
            _logger.Information("S3 storage directory {DirectoryPath} cleanup deleted {DeletedBatchCount} object(s). TotalDeletedObjectCount={DeletedObjectCount}.", directoryPath, keys.Count, deletedCount);
        }
    }

    public async Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var settings = ReadSettings(target.SettingsJson);
        var storagePath = S3TargetUrlBuilder.StoragePath(settings, path);
        using var client = await CreateClientAsync(target, cancellationToken);
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = storagePath,
            InputStream = stream,
            AutoCloseStream = false
        }, cancellationToken);
    }

    public async Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default)
    {
        var settings = ReadSettings(target.SettingsJson);
        var storagePath = S3TargetUrlBuilder.StoragePath(settings, path);
        using var client = await CreateClientAsync(target, cancellationToken);
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = settings.Bucket,
            Key = storagePath
        }, cancellationToken);

        using var output = new MemoryStream();
        await response.ResponseStream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    public async Task<IReadOnlyList<string>> ListObjectPathsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
        (await ListObjectsAsync(target, rootPath, cancellationToken)).Select(x => x.Path).ToList();

    public async Task<IReadOnlyList<BackupStorageObjectInfo>> ListObjectsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default)
    {
        var settings = ReadSettings(target.SettingsJson);
        var storagePrefix = S3TargetUrlBuilder.StoragePath(settings, rootPath).TrimStart('/');
        using var client = await CreateClientAsync(target, cancellationToken);
        var objects = await ListObjectInfoAsync(client, settings.Bucket, storagePrefix, cancellationToken);
        var targetPrefix = string.IsNullOrWhiteSpace(settings.PathPrefix) ? "" : settings.PathPrefix.Trim('/').Trim() + "/";
        return objects
            .Select(item => new BackupStorageObjectInfo(
                item.Key.StartsWith(targetPrefix, StringComparison.Ordinal) ? item.Key[targetPrefix.Length..] : item.Key,
                item.SizeBytes))
            .ToList();
    }

    public async Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default)
    {
        var settings = ReadSettings(target.SettingsJson);
        using var client = await CreateClientAsync(target, cancellationToken);
        await DeleteStoredObjectAsync(client, settings.Bucket, S3TargetUrlBuilder.StoragePath(settings, path).TrimStart('/'), cancellationToken);
    }

    public async Task<StorageConnectionTestResult> TestConnectionAsync(BackupTargetEntity target, CancellationToken cancellationToken = default)
    {
        var path = $".chobo-connection-tests/{Guid.NewGuid():N}.bin";
        var payload = Encoding.UTF8.GetBytes($"chobo-storage-test:{Guid.NewGuid():N}");
        try
        {
            await WriteObjectAsync(target, path, payload, cancellationToken);
            var read = await ReadObjectAsync(target, path, cancellationToken);
            if (!read.SequenceEqual(payload))
            {
                return new StorageConnectionTestResult(target.Id, target.Type, false, "S3 read returned different content than was written.");
            }

            await DeleteObjectAsync(target, path, cancellationToken);
            return new StorageConnectionTestResult(target.Id, target.Type, true, "S3 connection succeeded.");
        }
        catch (Exception ex)
        {
            try
            {
                await DeleteObjectAsync(target, path, CancellationToken.None);
            }
            catch
            {
                // Best-effort cleanup for a failed probe.
            }

            return new StorageConnectionTestResult(target.Id, target.Type, false, ex.Message);
        }
    }

    private string S3Endpoint(BackupTargetEntity target, string path) =>
        endpointRewrites.RewriteS3EndpointForClickHouse(S3TargetUrlBuilder.BuildObjectUrl(ReadSettings(target.SettingsJson), path)).ToString();

    private async Task<IAmazonS3> CreateClientAsync(BackupTargetEntity target, CancellationToken cancellationToken)
    {
        var settings = ReadSettings(target.SettingsJson);
        var secrets = await DecryptSecretsAsync(target, cancellationToken);
        if (string.IsNullOrWhiteSpace(secrets.AccessKey) || string.IsNullOrWhiteSpace(secrets.SecretKey))
        {
            throw new InvalidOperationException("S3 operations require target access key and secret key.");
        }

        var region = string.IsNullOrWhiteSpace(settings.Region) ? "us-east-1" : settings.Region;
        var endpoint = new Uri(settings.Endpoint.TrimEnd('/'));
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint.ToString(),
            AuthenticationRegion = region,
            ForcePathStyle = settings.ForcePathStyle,
            Timeout = options.CurrentValue.S3RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : options.CurrentValue.S3RequestTimeout,
            MaxErrorRetry = Math.Max(0, options.CurrentValue.S3MaxErrorRetry)
        };

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            config.UseHttp = true;
        }

        return new AmazonS3Client(new BasicAWSCredentials(secrets.AccessKey, secrets.SecretKey), config);
    }

    private async Task<S3DecryptedSecrets> DecryptSecretsAsync(BackupTargetEntity target, CancellationToken cancellationToken)
    {
        var stored = ReadStoredSecrets(target.SecretsJson);
        return new S3DecryptedSecrets(
            await protector.DecryptAsync(stored.AccessKey?.Ciphertext, stored.AccessKey?.KeyId, cancellationToken) ?? "",
            await protector.DecryptAsync(stored.SecretKey?.Ciphertext, stored.SecretKey?.KeyId, cancellationToken) ?? "");
    }

    private async Task<S3StoredSecrets> ProtectSecretsAsync(S3TargetSecretsDto secrets, CancellationToken cancellationToken)
    {
        var accessKey = await protector.EncryptAsync(secrets.AccessKey, cancellationToken);
        var secretKey = await protector.EncryptAsync(secrets.SecretKey, cancellationToken);
        return new S3StoredSecrets(ToStored(accessKey), ToStored(secretKey));
    }


    private async Task<S3StoredSecrets> ProtectSecretsAsync(S3TargetSecretsDto secrets, S3StoredSecrets existing, CancellationToken cancellationToken)
    {
        var accessKey = secrets.AccessKey is null ? existing.AccessKey : ToStored(await protector.EncryptAsync(secrets.AccessKey, cancellationToken));
        var secretKey = secrets.SecretKey is null ? existing.SecretKey : ToStored(await protector.EncryptAsync(secrets.SecretKey, cancellationToken));
        return new S3StoredSecrets(accessKey, secretKey);
    }

    private static void ValidateSecrets(S3TargetSecretsDto secrets, bool requireCredentials)
    {
        var hasAccessKey = !string.IsNullOrWhiteSpace(secrets.AccessKey);
        var hasSecretKey = !string.IsNullOrWhiteSpace(secrets.SecretKey);
        if (requireCredentials && (!hasAccessKey || !hasSecretKey))
        {
            throw new ArgumentException("S3 access key and secret key are required.");
        }

        if (hasAccessKey != hasSecretKey)
        {
            throw new ArgumentException("S3 access key and secret key must be provided together.");
        }
    }
    private static StoredSecret? ToStored(ProtectedSecret? secret) =>
        secret is null ? null : new StoredSecret(secret.Ciphertext, secret.KeyId);

    private static S3TargetSettingsDto ReadSettings(IReadOnlyDictionary<string, JsonElement>? settings)
    {
        if (settings is null)
        {
            return new S3TargetSettingsDto("", "us-east-1", "", null, true);
        }

        return new S3TargetSettingsDto(
            GetString(settings, "endpoint") ?? "",
            GetString(settings, "region") ?? "us-east-1",
            GetString(settings, "bucket") ?? "",
            GetString(settings, "pathPrefix"),
            GetBool(settings, "forcePathStyle") ?? true);
    }

    private static S3TargetSecretsDto ReadSecrets(IReadOnlyDictionary<string, JsonElement>? secrets)
    {
        if (secrets is null)
        {
            return new S3TargetSecretsDto(null, null);
        }

        return new S3TargetSecretsDto(GetString(secrets, "accessKey"), GetString(secrets, "secretKey"));
    }

    private static S3TargetSettingsDto ReadSettings(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new S3TargetSettingsDto("", "us-east-1", "", null, true)
            : JsonSerializer.Deserialize<S3TargetSettingsDto>(json, JsonOptions) ?? new S3TargetSettingsDto("", "us-east-1", "", null, true);

    private static S3StoredSecrets ReadStoredSecrets(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new S3StoredSecrets(null, null)
            : JsonSerializer.Deserialize<S3StoredSecrets>(json, JsonOptions) ?? new S3StoredSecrets(null, null);

    private static IReadOnlyDictionary<string, JsonElement> ToDictionary<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, JsonOptions));
        return document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var element) && element.ValueKind != JsonValueKind.Null ? element.GetString() : null;

    private static bool? GetBool(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : null;

    private static void ValidateSettings(string name, S3TargetSettingsDto settings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.");
        }
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("Endpoint is required.");
        }
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URI.");
        }
        if (string.IsNullOrWhiteSpace(settings.Bucket))
        {
            throw new ArgumentException("Bucket is required.");
        }
    }

    private static async Task<IReadOnlyList<string>> ListKeyPageAsync(IAmazonS3 client, string bucket, string prefix, int maxKeys, CancellationToken cancellationToken)
    {
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix,
            MaxKeys = maxKeys
        }, cancellationToken);

        return (response.S3Objects ?? Enumerable.Empty<S3Object>())
            .Where(x => !string.IsNullOrEmpty(x.Key) && x.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(x => x.Key!)
            .ToList();
    }

    private static async Task<IReadOnlyList<(string Key, long SizeBytes)>> ListObjectInfoAsync(IAmazonS3 client, string bucket, string prefix, CancellationToken cancellationToken)
    {
        var objects = new List<(string Key, long SizeBytes)>();
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken
            }, cancellationToken);
            objects.AddRange((response.S3Objects ?? Enumerable.Empty<S3Object>())
                .Where(x => !string.IsNullOrEmpty(x.Key) && x.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(x => (x.Key!, x.Size ?? 0)));
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (!string.IsNullOrEmpty(continuationToken));

        return objects;
    }

    private static async Task DeleteStoredObjectsAsync(IAmazonS3 client, string bucket, IReadOnlyList<string> storagePaths, CancellationToken cancellationToken)
    {
        if (storagePaths.Count == 0)
        {
            return;
        }

        try
        {
            await client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = storagePaths.Select(path => new KeyVersion { Key = path }).ToList(),
                Quiet = true
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (IsMissingObjectDeleteFailure(ex))
        {
        }
    }

    private static async Task DeleteStoredObjectAsync(IAmazonS3 client, string bucket, string storagePath, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = storagePath
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    private static bool IsMissingObjectDeleteFailure(AmazonS3Exception ex)
    {
        if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return true;
        }

        return string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("NoSuchKey", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StoredSecret(string? Ciphertext, Guid? KeyId);
    private sealed record S3StoredSecrets(StoredSecret? AccessKey, StoredSecret? SecretKey);
    private sealed record S3DecryptedSecrets(string AccessKey, string SecretKey);
}
