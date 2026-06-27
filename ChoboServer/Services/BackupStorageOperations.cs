using System.Text;
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

public sealed class BackupStorageOperations(
    S3BackupStorageOperations s3) : IBackupStorageOperations
{
    public Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default) =>
        For(target).DeleteDirectoryAsync(target, directoryPath, cancellationToken);

    public Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default) =>
        For(target).WriteObjectAsync(target, path, content, cancellationToken);

    public Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
        For(target).ReadObjectAsync(target, path, cancellationToken);

    public Task<IReadOnlyList<string>> ListObjectPathsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
        For(target).ListObjectPathsAsync(target, rootPath, cancellationToken);

    public Task<IReadOnlyList<BackupStorageObjectInfo>> ListObjectsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
        For(target).ListObjectsAsync(target, rootPath, cancellationToken);

    public Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
        For(target).DeleteObjectAsync(target, path, cancellationToken);

    public Task<StorageConnectionTestResult> TestConnectionAsync(BackupTargetEntity target, CancellationToken cancellationToken = default) =>
        For(target).TestConnectionAsync(target, cancellationToken);

    private IBackupStorageOperations For(BackupTargetEntity target) =>
        target.Type switch
        {
            BackupTargetType.S3 => s3,
            _ => throw new NotSupportedException($"Backup target type '{target.Type}' is not supported.")
        };
}

public sealed record BackupStorageObjectInfo(string Path, long SizeBytes);

public sealed class S3BackupStorageOperations(ICredentialProtector protector, IOptionsMonitor<BackupStorageOperationOptions> options, Serilog.ILogger logger) : IBackupStorageOperations
{
    private readonly Serilog.ILogger _logger = logger.ForContext<S3BackupStorageOperations>();

    public async Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default)
    {
        var prefix = S3TargetUrlBuilder.StoragePath(target, directoryPath).TrimStart('/');
        var batchSize = Math.Clamp(options.CurrentValue.S3DeleteBatchSize, 1, 1000);
        var deletedCount = 0;
        using var client = await CreateClientAsync(target, cancellationToken);
        while (true)
        {
            var keys = await ListKeyPageAsync(client, target.Bucket, prefix, batchSize, cancellationToken);
            if (keys.Count == 0)
            {
                _logger.Information("S3 directory {DirectoryPath} cleanup completed. DeletedObjectCount={DeletedObjectCount}.", directoryPath, deletedCount);
                return;
            }

            await DeleteStoredObjectsAsync(client, target.Bucket, keys, cancellationToken);
            deletedCount += keys.Count;
            _logger.Information("S3 directory {DirectoryPath} cleanup deleted {DeletedBatchCount} object(s). TotalDeletedObjectCount={DeletedObjectCount}.", directoryPath, keys.Count, deletedCount);
        }
    }

    public async Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var storagePath = S3TargetUrlBuilder.StoragePath(target, path);
        using var client = await CreateClientAsync(target, cancellationToken);
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = target.Bucket,
            Key = storagePath,
            InputStream = stream,
            AutoCloseStream = false
        }, cancellationToken);
    }

    public async Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default)
    {
        var storagePath = S3TargetUrlBuilder.StoragePath(target, path);
        using var client = await CreateClientAsync(target, cancellationToken);
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = target.Bucket,
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
        var storagePrefix = S3TargetUrlBuilder.StoragePath(target, rootPath).TrimStart('/');
        using var client = await CreateClientAsync(target, cancellationToken);
        var objects = await ListObjectInfoAsync(client, target.Bucket, storagePrefix, cancellationToken);
        var targetPrefix = string.IsNullOrWhiteSpace(target.PathPrefix) ? "" : target.PathPrefix.Trim('/').Trim() + "/";
        return objects
            .Select(item => new BackupStorageObjectInfo(
                item.Key.StartsWith(targetPrefix, StringComparison.Ordinal) ? item.Key[targetPrefix.Length..] : item.Key,
                item.SizeBytes))
            .ToList();
    }

    public async Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default)
    {
        using var client = await CreateClientAsync(target, cancellationToken);
        await DeleteStoredObjectAsync(client, target.Bucket, S3TargetUrlBuilder.StoragePath(target, path).TrimStart('/'), cancellationToken);
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

    private async Task<IAmazonS3> CreateClientAsync(BackupTargetEntity target, CancellationToken cancellationToken)
    {
        var accessKey = await protector.DecryptAsync(target.EncryptedAccessKey, target.EncryptedAccessKeyKeyId, cancellationToken) ?? "";
        var secretKey = await protector.DecryptAsync(target.EncryptedSecretKey, target.EncryptedSecretKeyKeyId, cancellationToken) ?? "";
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("S3 operations require target access key and secret key.");
        }

        var region = string.IsNullOrWhiteSpace(target.Region) ? "us-east-1" : target.Region;
        var endpoint = new Uri(target.Endpoint.TrimEnd('/'));
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint.ToString(),
            AuthenticationRegion = region,
            ForcePathStyle = target.ForcePathStyle,
            Timeout = options.CurrentValue.S3RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : options.CurrentValue.S3RequestTimeout,
            MaxErrorRetry = Math.Max(0, options.CurrentValue.S3MaxErrorRetry)
        };

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            config.UseHttp = true;
        }

        return new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    }
}

