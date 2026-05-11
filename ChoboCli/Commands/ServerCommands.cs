using ChoboCli.Cli;
using ChoboCli.Infrastructure;

namespace ChoboCli.Commands;

public sealed class ServerCommands : CliSubject
{
    public ServerCommands()
    {
        Verb("auth", "Persist server URL and access token in the user profile.", AuthAsync);
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
}
