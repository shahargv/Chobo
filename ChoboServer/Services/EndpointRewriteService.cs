using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public interface IEndpointRewriteService
{
    ClickHouseNodeEndpoint RewriteClickHouseEndpointForServer(ClickHouseNodeEndpoint endpoint);
    Uri RewriteS3EndpointForClickHouse(Uri endpoint);
}

public sealed class EndpointRewriteService(IOptions<ChoboEndpointRewriteOptions> options) : IEndpointRewriteService
{
    private readonly ChoboEndpointRewriteOptions _options = options.Value;

    public ClickHouseNodeEndpoint RewriteClickHouseEndpointForServer(ClickHouseNodeEndpoint endpoint)
    {
        var rule = _options.ClickHouse.FirstOrDefault(rule => Matches(rule, endpoint));
        if (rule is null)
        {
            return endpoint;
        }

        return new ClickHouseNodeEndpoint(
            string.IsNullOrWhiteSpace(rule.ServerHost) ? endpoint.Host : rule.ServerHost,
            rule.ServerPort ?? endpoint.Port,
            rule.ServerUseTls ?? endpoint.UseTls);
    }

    public Uri RewriteS3EndpointForClickHouse(Uri endpoint)
    {
        foreach (var rule in _options.S3ForClickHouse)
        {
            if (!Uri.TryCreate(rule.ServerEndpoint, UriKind.Absolute, out var serverEndpoint) ||
                !Uri.TryCreate(rule.ClickHouseEndpoint, UriKind.Absolute, out var clickHouseEndpoint))
            {
                continue;
            }

            if (!SameAuthority(endpoint, serverEndpoint))
            {
                continue;
            }

            var builder = new UriBuilder(endpoint)
            {
                Scheme = clickHouseEndpoint.Scheme,
                Host = clickHouseEndpoint.Host,
                Port = clickHouseEndpoint.IsDefaultPort ? -1 : clickHouseEndpoint.Port
            };
            return builder.Uri;
        }

        return endpoint;
    }

    private static bool Matches(ClickHouseEndpointRewriteOptions rule, ClickHouseNodeEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(rule.Host) || string.IsNullOrWhiteSpace(rule.ServerHost))
        {
            return false;
        }

        return string.Equals(rule.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
               (rule.Port is null || rule.Port == endpoint.Port) &&
               (rule.UseTls is null || rule.UseTls == endpoint.UseTls);
    }

    private static bool SameAuthority(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}
