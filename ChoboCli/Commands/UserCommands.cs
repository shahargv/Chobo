using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class UserCommands : CliSubject
{
    public UserCommands()
    {
        Verb("list", "List users.", ListAsync);
        Verb("add", "Add a user and print the one-time access token.", AddAsync);
        Verb("remove", "Deactivate a user.", RemoveAsync);
        Verb("tokens", "List access-token metadata for a user.", TokensAsync);
        Verb("add-token", "Add a named access token for a user.", AddTokenAsync);
        Verb("remove-token", "Deactivate one access token for a user.", RemoveTokenAsync);
    }

    public override string Name => "users";
    public override string Description => "User and access-token bootstrap operations.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("users"));

    private static Task<object?> AddAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("users", new CreateUserRequest(context.Command.Options.Required("--username"))));

    private static Task<object?> RemoveAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"users/{context.Command.Options.Required("--id")}"));

    private static Task<object?> TokensAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync($"users/{context.Command.Options.Required("--id")}/tokens"));

    private static Task<object?> AddTokenAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync(
            $"users/{context.Command.Options.Required("--id")}/tokens",
            new CreateAccessTokenRequest(context.Command.Options.Required("--name"))));

    private static Task<object?> RemoveTokenAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"users/{context.Command.Options.Required("--id")}/tokens/{context.Command.Options.Required("--token-id")}"));
}
