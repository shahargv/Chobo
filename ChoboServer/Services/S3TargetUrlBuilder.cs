using Chobo.Contracts;

namespace ChoboServer.Services;

public static class S3TargetUrlBuilder
{
    public static Uri BuildObjectUrl(S3TargetSettingsDto settings, string path)
    {
        var endpoint = new Uri(settings.Endpoint.TrimEnd('/') + "/");
        var bucket = settings.Bucket.Trim('/');
        var objectPath = StoragePath(settings, path).TrimStart('/');
        var urlPath = settings.ForcePathStyle
            ? JoinPath(bucket, objectPath)
            : objectPath;
        var uriBuilder = new UriBuilder(endpoint)
        {
            Path = "/" + EncodePath(urlPath)
        };

        if (!settings.ForcePathStyle)
        {
            uriBuilder.Host = $"{bucket}.{endpoint.Host}";
        }

        return uriBuilder.Uri;
    }

    public static string StoragePath(S3TargetSettingsDto settings, string path)
    {
        var prefix = string.IsNullOrWhiteSpace(settings.PathPrefix) ? "" : settings.PathPrefix.Trim('/').Trim() + "/";
        return prefix + path.TrimStart('/');
    }

    private static string JoinPath(string first, string second) =>
        string.IsNullOrEmpty(second) ? first : $"{first}/{second}";

    private static string EncodePath(string value) =>
        string.Join("/", value.Split('/').Select(EncodeQuery));

    private static string EncodeQuery(string value) =>
        Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);
}
