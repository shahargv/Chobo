using ChoboServer.Data;

namespace ChoboServer.Services;

public static class S3TargetUrlBuilder
{
    public static Uri BuildObjectUrl(BackupTargetEntity target, string path)
    {
        var endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/");
        var bucket = target.Bucket.Trim('/');
        var objectPath = StoragePath(target, path).TrimStart('/');
        var urlPath = target.ForcePathStyle
            ? JoinPath(bucket, objectPath)
            : objectPath;
        var uriBuilder = new UriBuilder(endpoint)
        {
            Path = "/" + EncodePath(urlPath)
        };

        if (!target.ForcePathStyle)
        {
            uriBuilder.Host = $"{bucket}.{endpoint.Host}";
        }

        return uriBuilder.Uri;
    }

    public static string StoragePath(BackupTargetEntity target, string path)
    {
        var prefix = string.IsNullOrWhiteSpace(target.PathPrefix) ? "" : target.PathPrefix.Trim('/').Trim() + "/";
        return prefix + path.TrimStart('/');
    }

    private static string JoinPath(string first, string second) =>
        string.IsNullOrEmpty(second) ? first : $"{first}/{second}";

    private static string EncodePath(string value) =>
        string.Join("/", value.Split('/').Select(EncodeQuery));

    private static string EncodeQuery(string value) =>
        Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);
}
