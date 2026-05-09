using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Infrastructure;

public sealed class ChoboApiClientFactory
{
    public async Task<ChoboApiClient> CreateAsync(OptionBag options, ProfileStore profileStore)
    {
        var profile = profileStore.Load();
        var serverUrl = options.Optional("--server-url") ?? profile?.ServerUrl ?? throw new InvalidOperationException("Run ChoboCli server auth or pass --server-url.");
        var token = options.Optional("--access-token") ?? profile?.AccessToken ?? throw new InvalidOperationException("Run ChoboCli server auth or pass --access-token.");
        var client = new ChoboApiClient(serverUrl, token);
        try
        {
            await client.EnsureCompatibleServerAsync();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
