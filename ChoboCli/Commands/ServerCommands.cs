using Chobo.Contracts;
using ChoboCli.Cli;
using ChoboCli.Infrastructure;

namespace ChoboCli.Commands;

public sealed class ServerCommands : CliSubject
{
    public ServerCommands()
    {
        Verb("auth", "Persist server URL and access token in the user profile.", AuthAsync);
        Verb("install", "Finalize first-time installation and print the one-time initial token.", InstallAsync);
    }

    public override string Name => "server";
    public override string Description => "Server profile and connectivity commands.";

    private static async Task<object?> AuthAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--server-url", "--access-token");
        var profile = new CliProfile(
            required["--server-url"],
            required["--access-token"]);
        await context.Profiles.SaveAsync(profile);
        return $"Authenticated to {profile.ServerUrl}.";
    }

    private static async Task<object?> InstallAsync(CommandContext context)
    {
        var serverUrl = context.Command.Options.Optional("--server-url")
            ?? context.Profiles.Load()?.ServerUrl
            ?? throw new InvalidOperationException("Pass --server-url for first-time installation.");
        var adminUser = context.Command.Options.Optional("--admin-user");

        using var client = new ChoboApiClient(serverUrl);
        var result = await client.InstallAsync(new InstallRequest(adminUser));
        await context.Profiles.SaveAsync(new CliProfile(serverUrl, result.AccessToken));
        return result;
    }
}
