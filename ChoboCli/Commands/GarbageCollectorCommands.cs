using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class GarbageCollectorCommands : CliSubject
{
    public GarbageCollectorCommands()
    {
        Verb("status", "Show backup garbage collector status.", StatusAsync);
        Verb("run", "Run backup garbage collection now.", RunAsync);
    }

    public override string Name => "gc";
    public override string Description => "Backup garbage collector operations.";

    private static Task<object?> StatusAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("backups/garbage-collector/status"));

    private static Task<object?> RunAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("backups/garbage-collector/run", new { }));
}
