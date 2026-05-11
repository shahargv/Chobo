using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class RestoreCommand : CliSubject
{
    public RestoreCommand()
    {
        Verb("initiate", "Start a restore.", InitiateAsync);
    }

    public override string Name => "restore";
    public override string Description => "Start restore operations.";

    private static Task<object?> InitiateAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--backup-id", "--target-cluster-id");
        var request = new InitiateRestoreRequest(
            Guid.Parse(required["--backup-id"]),
            Guid.Parse(required["--target-cluster-id"]),
            context.Command.Options.Optional("--database"),
            context.Command.Options.Optional("--table"),
            context.Command.Options.Optional("--target-database"),
            context.Command.Options.Optional("--target-table"),
            context.Command.Options.Has("--append"),
            context.Command.Options.Has("--allow-schema-mismatch"));
        return CommandHelpers.WithClient(context, client => client.PostAsync("restores/initiate", request));
    }
}

public sealed class RestoresCommands : CliSubject
{
    public RestoresCommands()
    {
        Verb("list", "List restores.", ListAsync);
        Verb("show", "Show one restore.", ShowAsync);
        Verb("wait", "Wait for a restore to finish.", WaitAsync);
    }

    public override string Name => "restores";
    public override string Description => "Inspect restore operations.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("restores"));

    private static Task<object?> ShowAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync($"restores/{context.Command.Options.Required("--id")}"));

    private static async Task<object?> WaitAsync(CommandContext context)
    {
        using var client = await context.CreateClientAsync();
        var id = context.Command.Options.Required("--id");
        var timeout = TimeSpan.FromSeconds(context.Command.Options.Int("--timeout-seconds", 300));
        var interval = TimeSpan.FromSeconds(context.Command.Options.Int("--poll-seconds", 2));
        var deadline = DateTimeOffset.UtcNow + timeout;
        RestoreDto? current = null;
        Exception? lastError = null;
        do
        {
            try
            {
                current = await client.GetOptionalAsync<RestoreDto>($"restores/{id}");
                lastError = null;
                if (current is not null && IsTerminal(current.Status))
                {
                    return current;
                }
            }
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastError = ex;
            }

            await Task.Delay(interval);
        } while (DateTimeOffset.UtcNow < deadline);

        if (current is null && lastError is not null)
        {
            throw lastError;
        }

        return current;
    }

    private static bool IsTerminal(RestoreRunStatus status) =>
        status is RestoreRunStatus.Succeeded or RestoreRunStatus.Failed or RestoreRunStatus.Canceled;
}
