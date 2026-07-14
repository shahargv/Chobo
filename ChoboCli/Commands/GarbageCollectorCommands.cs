using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class GarbageCollectorCommands : CliSubject
{
    public GarbageCollectorCommands()
    {
        Verb("status", "Show backup garbage collector status.", StatusAsync);
        Verb("queue", "Show backup garbage collector queue.", QueueAsync);
        Verb("explain", "Explain why a backup is or is not eligible for garbage collection. Options: --id <backup-id>", ExplainAsync);
        Verb("run", "Run backup garbage collection now.", RunAsync);
        Verb("run-one", "Run backup garbage collection for one queued backup. Options: --id <backup-id>", RunOneAsync);
    }

    public override string Name => "gc";
    public override string Description => "Backup garbage collector operations.";

    private static Task<object?> StatusAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("backups/garbage-collector/status"));

    private static Task<object?> QueueAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("backups/garbage-collector/queue"));

    private static Task<object?> ExplainAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync($"backups/{context.Command.Options.Required("--id")}/garbage-collection-evaluation"));

    private static Task<object?> RunAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("backups/garbage-collector/run", new { }));

    private static Task<object?> RunOneAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"backups/garbage-collector/run/{context.Command.Options.Required("--id")}", new { }));
}
