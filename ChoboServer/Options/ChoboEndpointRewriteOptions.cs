namespace ChoboServer.Options;

public sealed class ChoboEndpointRewriteOptions
{
    public List<ClickHouseEndpointRewriteOptions> ClickHouse { get; set; } = [];
    public List<S3EndpointRewriteOptions> S3ForClickHouse { get; set; } = [];
}

public sealed class ClickHouseEndpointRewriteOptions
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public bool? UseTls { get; set; }
    public string? ServerHost { get; set; }
    public int? ServerPort { get; set; }
    public bool? ServerUseTls { get; set; }
}

public sealed class S3EndpointRewriteOptions
{
    public string? ServerEndpoint { get; set; }
    public string? ClickHouseEndpoint { get; set; }
}
