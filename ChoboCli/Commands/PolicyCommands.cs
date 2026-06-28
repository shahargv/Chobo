using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class PolicyCommands : CliSubject
{
    public PolicyCommands()
    {
        Verb("list", "List backup policies.", ListAsync);
        Verb("add", "Add a backup policy.", AddAsync);
        Verb("update", "Update a backup policy.", UpdateAsync);
        Verb("remove", "Soft-delete a backup policy.", RemoveAsync);
        Verb("evaluate", "Evaluate a backup policy selector.", EvaluateAsync);
    }

    public override string Name => "policies";
    public override string Description => "Backup policy configuration.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("policies" + IncludeDeletedQuery(context)));

    private static Task<object?> AddAsync(CommandContext context)
    {
        var request = Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PostAsync("policies", request));
    }

    private static Task<object?> UpdateAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--id");
        var request = Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PutAsync($"policies/{required["--id"]}", request));
    }

    private static Task<object?> RemoveAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"policies/{context.Command.Options.Required("--id")}"));

    private static Task<object?> EvaluateAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"policies/{context.Command.Options.Required("--id")}/evaluate", CommandHelpers.PolicyEvaluationRequestFromOption(context.Command.Options)));

    private static UpsertPolicyRequest Request(OptionBag options)
    {
        var required = options.Require("--name", "--source-cluster-id");
        var contentMode = options.Has("--schema-only") ? BackupContentMode.SchemaOnly : BackupContentMode.SchemaAndData;
        var targetId = options.Optional("--target-id") is { } rawTargetId ? Guid.Parse(rawTargetId) : (Guid?)null;
        if (contentMode == BackupContentMode.SchemaAndData && targetId is null)
        {
            throw new InvalidOperationException("--target-id is required for schema+data policies.");
        }

        return new UpsertPolicyRequest(
            required["--name"],
            Guid.Parse(required["--source-cluster-id"]),
            targetId,
            CommandHelpers.PolicySelectorFromOption(options),
            contentMode,
            Retention(options),
            options.Optional("--failed-backup-retention-mode") is { } mode
                ? Enum.Parse<FailedBackupRetentionMode>(mode, ignoreCase: true)
                : FailedBackupRetentionMode.KeepAndExcludeFromMinBackupsToKeep,
            CommandHelpers.ClickHouseSettingsFromOptions(options, "backup"),
            CommandHelpers.ClickHouseSettingsFromOptions(options, "restore"));
    }

    private static BackupRetentionDto? Retention(OptionBag options)
    {
        var fullRetentionMinutes = options.Optional("--full-retention-minutes");
        var incrementalRetentionMinutes = options.Optional("--incremental-retention-minutes");
        if (fullRetentionMinutes is null && incrementalRetentionMinutes is null)
        {
            return null;
        }

        return new BackupRetentionDto(
            fullRetentionMinutes is null ? null : int.Parse(fullRetentionMinutes),
            incrementalRetentionMinutes is null ? null : int.Parse(incrementalRetentionMinutes),
            options.Optional("--min-backups-to-keep") is { } minBackupsToKeep ? int.Parse(minBackupsToKeep) : 0,
            options.Optional("--min-full-backups-to-keep") is { } minFullBackupsToKeep ? int.Parse(minFullBackupsToKeep) : 0);
    }
    private static string IncludeDeletedQuery(CommandContext context) =>
        context.Command.Options.Has("--include-deleted") ? "?includeDeleted=true" : "";
}
