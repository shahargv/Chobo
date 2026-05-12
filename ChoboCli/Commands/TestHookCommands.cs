using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class TestHookCommands : CliSubject
{
    public TestHookCommands()
    {
        Verb("seed-missing-backup-operation", "Seed a test backup whose ClickHouse operation is missing from system.backups.", SeedMissingBackupOperationAsync);
        Verb("delay-next-backup-before-poll", "Delay the next backup after operation id persistence.", DelayNextBackupBeforePollAsync);
        Verb("delay-next-restore-before-poll", "Delay the next restore after operation id persistence.", DelayNextRestoreBeforePollAsync);
        Verb("crash", "Crash the server process.", CrashAsync);
    }

    public override string Name => "test-hooks";
    public override string Description => "Test-only server hooks.";

    private static Task<object?> SeedMissingBackupOperationAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--source-cluster-id", "--target-id", "--database", "--table");
        var request = new SeedMissingBackupOperationRequest(
            Guid.Parse(required["--source-cluster-id"]),
            Guid.Parse(required["--target-id"]),
            required["--database"],
            required["--table"],
            context.Command.Options.Int("--shard-count", 1));
        return CommandHelpers.WithClient(context, client => client.PostAsync("test-hooks/seed-missing-backup-operation", request));
    }

    private static Task<object?> DelayNextBackupBeforePollAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("test-hooks/delay-next-backup-before-poll", new { }));

    private static Task<object?> DelayNextRestoreBeforePollAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("test-hooks/delay-next-restore-before-poll", new { }));

    private static Task<object?> CrashAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("test-hooks/crash", new { }));
}
