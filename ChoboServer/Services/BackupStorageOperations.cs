using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Chobo.Contracts;
using ChoboServer.Data;

namespace ChoboServer.Services;

public interface IBackupStorageOperations
{
    Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default);
    Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default);
    Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default);
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

public sealed class S3BackupStorageOperations(ICredentialProtector protector, Serilog.ILogger logger) : IBackupStorageOperations
{
    private static readonly string EmptyPayloadHash = HashHex(Array.Empty<byte>());
    private readonly Serilog.ILogger _logger = logger.ForContext<S3BackupStorageOperations>();

    public async Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default)
    {
        var prefix = StoragePath(target, directoryPath).TrimStart('/');
        while (true)
        {
            var keys = await ListKeysAsync(target, prefix, cancellationToken);
            if (keys.Count == 0)
            {
                _logger.Information("S3 directory {DirectoryPath} has no remaining objects.", directoryPath);
                return;
            }

            foreach (var key in keys)
            {
                await DeleteStoredObjectAsync(target, key, cancellationToken);
            }
        }
    }

    public async Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var storagePath = StoragePath(target, path);
        var payloadHash = HashHex(content);
        using var request = await CreateRequestAsync(target, HttpMethod.Put, storagePath, new SortedDictionary<string, string>(StringComparer.Ordinal), payloadHash, cancellationToken);
        request.Content = new ByteArrayContent(content);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync("S3 write request failed", response, cancellationToken);
        }
    }

    public async Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default)
    {
        var storagePath = StoragePath(target, path);
        using var request = await CreateRequestAsync(target, HttpMethod.Get, storagePath, new SortedDictionary<string, string>(StringComparer.Ordinal), EmptyPayloadHash, cancellationToken);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync("S3 read request failed", response, cancellationToken);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
        DeleteStoredObjectAsync(target, StoragePath(target, path).TrimStart('/'), cancellationToken);

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

    private async Task<IReadOnlyList<string>> ListKeysAsync(BackupTargetEntity target, string prefix, CancellationToken cancellationToken)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["list-type"] = "2",
            ["prefix"] = prefix
        };
        using var request = await CreateRequestAsync(target, HttpMethod.Get, "", query, EmptyPayloadHash, cancellationToken);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync("S3 list request failed", response, cancellationToken);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var document = XDocument.Parse(text);
        return document.Descendants()
            .Where(x => x.Name.LocalName == "Key")
            .Select(x => x.Value)
            .Where(x => x.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
    }

    private async Task DeleteStoredObjectAsync(BackupTargetEntity target, string storagePath, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(target, HttpMethod.Delete, storagePath, new SortedDictionary<string, string>(StringComparer.Ordinal), EmptyPayloadHash, cancellationToken);
        using var response = await SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        throw await FailureAsync("S3 delete request failed", response, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(BackupTargetEntity target, HttpMethod method, string path, SortedDictionary<string, string> query, string payloadHash, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/");
        var bucket = target.Bucket.Trim('/');
        var canonicalUri = "/" + EncodePath(bucket + (string.IsNullOrEmpty(path) ? "" : "/" + path.TrimStart('/')));
        var canonicalQuery = string.Join("&", query.Select(x => $"{EncodeQuery(x.Key)}={EncodeQuery(x.Value)}"));
        var uriBuilder = new UriBuilder(endpoint)
        {
            Path = canonicalUri,
            Query = canonicalQuery
        };
        var request = new HttpRequestMessage(method, uriBuilder.Uri);
        await SignRequestAsync(request, target, canonicalUri, canonicalQuery, payloadHash, cancellationToken);
        return request;
    }

    private async Task SignRequestAsync(HttpRequestMessage request, BackupTargetEntity target, string canonicalUri, string canonicalQuery, string payloadHash, CancellationToken cancellationToken)
    {
        var accessKey = await protector.DecryptAsync(target.EncryptedAccessKey, target.EncryptedAccessKeyKeyId, cancellationToken) ?? "";
        var secretKey = await protector.DecryptAsync(target.EncryptedSecretKey, target.EncryptedSecretKeyKeyId, cancellationToken) ?? "";
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("S3 operations require target access key and secret key.");
        }

        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var region = string.IsNullOrWhiteSpace(target.Region) ? "us-east-1" : target.Region;
        var host = request.RequestUri!.IsDefaultPort ? request.RequestUri.Host : request.RequestUri.Authority;
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        var canonicalRequest = string.Join('\n', request.Method.Method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", amzDate, scope, HashHex(Encoding.UTF8.GetBytes(canonicalRequest)));
        var signature = ToHex(Hmac(SigningKey(secretKey, date, region), stringToSign));

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<InvalidOperationException> FailureAsync(string prefix, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new InvalidOperationException($"{prefix}: {(int)response.StatusCode} {response.ReasonPhrase}. {text}");
    }

    private static string StoragePath(BackupTargetEntity target, string path)
    {
        var prefix = string.IsNullOrWhiteSpace(target.PathPrefix) ? "" : target.PathPrefix.Trim('/').Trim() + "/";
        return prefix + path.TrimStart('/');
    }

    private static string EncodePath(string value) =>
        string.Join("/", value.Split('/').Select(EncodeQuery));

    private static string EncodeQuery(string value) =>
        Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);

    private static byte[] SigningKey(string secretKey, string date, string region)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, "s3");
        return Hmac(kService, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string value) =>
        new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(value));

    private static string HashHex(byte[] bytes) =>
        ToHex(SHA256.HashData(bytes));

    private static string ToHex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
