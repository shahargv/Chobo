using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class BackupCommand : CliSubject
{
    public BackupCommand()
    {
        Verb("manual", "Start a manual backup.", ManualAsync);
    }

    public override string Name => "backup";
    public override string Description => "Start backup operations.";

    private static Task<object?> ManualAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("backups/manual", new ManualBackupRequest(
            Guid.Parse(context.Command.Options.Required("--cluster-id")),
            Guid.Parse(context.Command.Options.Required("--target-id")),
            CommandHelpers.PolicySelectorFromOption(context.Command.Options))));
}

public sealed class BackupsCommands : CliSubject
{
    public BackupsCommands()
    {
        Verb("list", "List backups.", ListAsync);
        Verb("show", "Show one backup.", ShowAsync);
        Verb("wait", "Wait for a backup to finish.", WaitAsync);
    }

    public override string Name => "backups";
    public override string Description => "Inspect backup operations.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("backups" + BackupQuery(context.Command.Options)));

    private static Task<object?> ShowAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync($"backups/{context.Command.Options.Required("--id")}"));

    private static async Task<object?> WaitAsync(CommandContext context)
    {
        using var client = await context.CreateClientAsync();
        var id = context.Command.Options.Required("--id");
        var timeout = TimeSpan.FromSeconds(context.Command.Options.Int("--timeout-seconds", 300));
        var interval = TimeSpan.FromSeconds(context.Command.Options.Int("--poll-seconds", 2));
        var deadline = DateTimeOffset.UtcNow + timeout;
        BackupDto? current = null;
        Exception? lastError = null;
        do
        {
            try
            {
                current = await client.GetOptionalAsync<BackupDto>($"backups/{id}");
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

    private static string BackupQuery(OptionBag options)
    {
        var query = new List<string>();
        if (options.Optional("--policy-id") is { } policyId) query.Add($"policyId={Uri.EscapeDataString(policyId)}");
        if (options.Optional("--cluster-name") is { } clusterName) query.Add($"clusterName={Uri.EscapeDataString(clusterName)}");
        if (options.Optional("--table-name") is { } tableName) query.Add($"tableName={Uri.EscapeDataString(tableName)}");
        if (options.Optional("--status") is { } status) query.Add($"status={Uri.EscapeDataString(status)}");
        return query.Count == 0 ? "" : "?" + string.Join("&", query);
    }

    private static bool IsTerminal(BackupRunStatus status) =>
        status is BackupRunStatus.Succeeded or BackupRunStatus.Failed or BackupRunStatus.Canceled;
}
