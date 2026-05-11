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
            await EnsureCompatibleServerWithRetryAsync(client);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task EnsureCompatibleServerWithRetryAsync(ChoboApiClient client)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await client.EnsureCompatibleServerAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == 3)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }

        throw last ?? new InvalidOperationException("Unable to verify server version.");
    }
}
