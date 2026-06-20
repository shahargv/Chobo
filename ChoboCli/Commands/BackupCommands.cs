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

    private static Task<object?> ManualAsync(CommandContext context)
    {
        var policyId = context.Command.Options.Optional("--policy-id") is { } policy ? Guid.Parse(policy) : (Guid?)null;
        var backupType = context.Command.Options.Enum("--backup-type", BackupType.Full);
        var required = policyId is null
            ? context.Command.Options.Require("--cluster-id", "--target-id")
            : new Dictionary<string, string>
            {
                ["--cluster-id"] = context.Command.Options.Optional("--cluster-id") ?? Guid.Empty.ToString(),
                ["--target-id"] = context.Command.Options.Optional("--target-id") ?? Guid.Empty.ToString()
            };
        var request = new ManualBackupRequest(
            Guid.Parse(required["--cluster-id"]),
            Guid.Parse(required["--target-id"]),
            CommandHelpers.PolicySelectorFromOption(context.Command.Options),
            backupType,
            policyId,
            context.Command.Options.Has("--schema-only"));
        return CommandHelpers.WithClient(context, client => client.PostAsync("backups/manual", request));
    }
}

public sealed class BackupsCommands : CliSubject
{
    public BackupsCommands()
    {
        Verb("list", "List backups.", ListAsync);
        Verb("show", "Show one backup.", ShowAsync);
        Verb("wait", "Wait for a backup to finish.", WaitAsync);
        Verb("delete", "Request deletion for one backup.", DeleteAsync);
        Verb("cancel", "Cancel a queued or running backup.", CancelAsync);
        Verb("pin", "Pin one backup.", PinAsync);
        Verb("unpin", "Unpin one backup.", UnpinAsync);
        Verb("recover", "Recover backup metadata from storage manifests.", RecoverAsync);
    }

    public override string Name => "backups";
    public override string Description => "Inspect backup operations.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("backups" + BackupQuery(context.Command.Options)));

    private static Task<object?> ShowAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync($"backups/{context.Command.Options.Required("--id")}"));

    private static Task<object?> DeleteAsync(CommandContext context)
    {
        var id = context.Command.Options.Required("--id");
        var force = context.Command.Options.Has("--force") ? "?force=true" : "";
        return CommandHelpers.WithClient(context, client => client.DeleteAsync($"backups/{id}{force}"));
    }

    private static Task<object?> CancelAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"backups/{context.Command.Options.Required("--id")}/cancel", new { }));
    private static Task<object?> PinAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"backups/{context.Command.Options.Required("--id")}/pin", new { }));

    private static Task<object?> UnpinAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"backups/{context.Command.Options.Required("--id")}/unpin", new { }));

    private static Task<object?> RecoverAsync(CommandContext context)
    {
        var targetId = Guid.Parse(context.Command.Options.Required("--target-id"));
        var backupPath = context.Command.Options.Optional("--backup-path");
        var scanRoot = context.Command.Options.Optional("--scan-root");
        if (!string.IsNullOrWhiteSpace(backupPath) == !string.IsNullOrWhiteSpace(scanRoot))
        {
            throw new InvalidOperationException("Specify exactly one of --backup-path or --scan-root.");
        }

        return CommandHelpers.WithClient(context, client =>
            !string.IsNullOrWhiteSpace(backupPath)
                ? client.PostAsync("backups/recover/from-path", new RecoverBackupMetadataFromPathRequest(targetId, backupPath))
                : client.PostAsync("backups/recover/scan", new RecoverBackupMetadataScanRequest(targetId, scanRoot!)));
    }

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
        status is BackupRunStatus.Succeeded or
            BackupRunStatus.PartiallySucceeded or
            BackupRunStatus.Failed or
            BackupRunStatus.Canceled or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleted;
}

