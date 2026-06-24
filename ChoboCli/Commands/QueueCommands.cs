using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class QueueCommands : CliSubject
{
    public QueueCommands()
    {
        Verb("list", "List active backup/restore shard queue rows.", ListAsync);
        Verb("move", "Move one queued shard queue row.", MoveAsync);
        Verb("move-table", "Move queued shard queue rows for a table.", MoveTableAsync);
        Verb("force", "Force one queued shard queue row to run next.", ForceAsync);
    }

    public override string Name => "queue";
    public override string Description => "Inspect and control backup/restore shard queue.";

    private static Task<object?> ListAsync(CommandContext context)
    {
        var kind = context.Command.Options.Optional("--kind") ?? "All";
        var status = context.Command.Options.Optional("--status") ?? "active";
        return CommandHelpers.WithClient(context, client => client.GetAsync($"queue?kind={Uri.EscapeDataString(kind)}&status={Uri.EscapeDataString(status)}"));
    }

    private static Task<object?> MoveAsync(CommandContext context)
    {
        var id = context.Command.Options.Required("--id");
        var request = new MoveQueueItemRequest(ParseDirection(context.Command.Options.Required("--direction")));
        return CommandHelpers.WithClient(context, client => client.PostAsync($"queue/items/{id}/move", request));
    }

    private static Task<object?> MoveTableAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--kind", "--table-id", "--direction");
        var request = new MoveQueueItemRequest(ParseDirection(required["--direction"]));
        return CommandHelpers.WithClient(context, client => client.PostAsync($"queue/tables/{required["--kind"]}/{required["--table-id"]}/move", request));
    }

    private static Task<object?> ForceAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"queue/items/{context.Command.Options.Required("--id")}/force", new { }));

    private static BackupRestoreQueueMoveDirection ParseDirection(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "up" => BackupRestoreQueueMoveDirection.Up,
            "down" => BackupRestoreQueueMoveDirection.Down,
            "top" => BackupRestoreQueueMoveDirection.Top,
            "bottom" => BackupRestoreQueueMoveDirection.Bottom,
            _ => Enum.Parse<BackupRestoreQueueMoveDirection>(value, ignoreCase: true)
        };
}