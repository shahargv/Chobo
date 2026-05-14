using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Chobo.Contracts;
using ChoboServer.Data;

namespace ChoboServer.Services;

public interface IBackupStorageDeletionService
{
    Task DeleteBackupDataAsync(BackupEntity backup, CancellationToken cancellationToken = default);
}

public sealed class BackupStorageDeletionService(
    S3BackupStorageDeleter s3,
    Serilog.ILogger logger) : IBackupStorageDeletionService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupStorageDeletionService>();

    public Task DeleteBackupDataAsync(BackupEntity backup, CancellationToken cancellationToken = default)
    {
        if (backup.Target is null)
        {
            throw new InvalidOperationException("Backup target is required for deletion.");
        }

        return backup.Target.Type switch
        {
            BackupTargetType.S3 => s3.DeleteBackupDataAsync(backup, cancellationToken),
            _ => throw new NotSupportedException($"Backup target type '{backup.Target.Type}' does not support deletion.")
        };
    }
}

public sealed class S3BackupStorageDeleter(CredentialProtector protector, Serilog.ILogger logger)
{
    private static readonly string EmptyPayloadHash = HashHex("");
    private readonly Serilog.ILogger _logger = logger.ForContext<S3BackupStorageDeleter>();

    public async Task DeleteBackupDataAsync(BackupEntity backup, CancellationToken cancellationToken = default)
    {
        if (backup.Target is null)
        {
            throw new InvalidOperationException("Backup target is required for S3 deletion.");
        }

        var prefixes = BackupPrefixes(backup).ToList();
        foreach (var prefix in prefixes)
        {
            await DeletePrefixAsync(backup.Target, prefix, cancellationToken);
        }
    }

    private async Task DeletePrefixAsync(BackupTargetEntity target, string backupPath, CancellationToken cancellationToken)
    {
        var prefix = StorageKey(target, backupPath).TrimStart('/');
        while (true)
        {
            var keys = await ListKeysAsync(target, prefix, cancellationToken);
            if (keys.Count == 0)
            {
                _logger.Information("S3 backup prefix {Prefix} has no remaining objects.", prefix);
                return;
            }

            foreach (var key in keys)
            {
                await DeleteObjectAsync(target, key, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<string>> ListKeysAsync(BackupTargetEntity target, string prefix, CancellationToken cancellationToken)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["list-type"] = "2",
            ["prefix"] = prefix
        };
        using var request = CreateRequest(target, HttpMethod.Get, "", query);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var document = XDocument.Parse(text);
        return document.Descendants()
            .Where(x => x.Name.LocalName == "Key")
            .Select(x => x.Value)
            .Where(x => x.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
    }

    private async Task DeleteObjectAsync(BackupTargetEntity target, string key, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(target, HttpMethod.Delete, key, new SortedDictionary<string, string>(StringComparer.Ordinal));
        using var response = await SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        throw await FailureAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(BackupTargetEntity target, HttpMethod method, string key, SortedDictionary<string, string> query)
    {
        var endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/");
        var bucket = target.Bucket.Trim('/');
        var canonicalUri = "/" + EncodePath(bucket + (string.IsNullOrEmpty(key) ? "" : "/" + key.TrimStart('/')));
        var canonicalQuery = string.Join("&", query.Select(x => $"{EncodeQuery(x.Key)}={EncodeQuery(x.Value)}"));
        var uriBuilder = new UriBuilder(endpoint)
        {
            Path = canonicalUri,
            Query = canonicalQuery
        };
        var request = new HttpRequestMessage(method, uriBuilder.Uri);
        SignRequest(request, target, canonicalUri, canonicalQuery);
        return request;
    }

    private void SignRequest(HttpRequestMessage request, BackupTargetEntity target, string canonicalUri, string canonicalQuery)
    {
        var accessKey = protector.Unprotect(target.EncryptedAccessKey) ?? "";
        var secretKey = protector.Unprotect(target.EncryptedSecretKey) ?? "";
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("S3 deletion requires target access key and secret key.");
        }

        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var region = string.IsNullOrWhiteSpace(target.Region) ? "us-east-1" : target.Region;
        var host = request.RequestUri!.IsDefaultPort ? request.RequestUri.Host : request.RequestUri.Authority;
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{EmptyPayloadHash}\nx-amz-date:{amzDate}\n";
        var canonicalRequest = string.Join('\n', request.Method.Method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, EmptyPayloadHash);
        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", amzDate, scope, HashHex(canonicalRequest));
        var signature = ToHex(Hmac(SigningKey(secretKey, date, region), stringToSign));

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", EmptyPayloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<InvalidOperationException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new InvalidOperationException($"S3 deletion request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {text}");
    }

    private static IEnumerable<string> BackupPrefixes(BackupEntity backup)
    {
        foreach (var table in backup.Tables)
        {
            var shardPaths = table.Shards.Select(x => x.S3Path).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            if (shardPaths.Count == 0)
            {
                yield return table.S3Path;
                continue;
            }

            foreach (var path in shardPaths)
            {
                yield return path;
            }
        }
    }

    private static string StorageKey(BackupTargetEntity target, string path)
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

    private static string HashHex(string value) =>
        ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ToHex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
