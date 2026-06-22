using System.Text;
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
        Verb("progress", "Show table and shard progress for one backup.", ProgressAsync);
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

    private static Task<object?> ProgressAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, async client =>
        {
            var backup = await client.GetAsync<BackupDto>($"backups/{context.Command.Options.Required("--id")}")
                ?? throw new InvalidOperationException("Backup was not found.");
            return FormatProgress(backup);
        });

    private static Task<object?> DeleteAsync(CommandContext context)
    {
        var id = context.Command.Options.Required("--id");
        var query = new List<string>();
        if (context.Command.Options.Has("--force")) query.Add("force=true");
        if (context.Command.Options.Has("--confirm-destructive")) query.Add("confirmDestructive=true");
        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return CommandHelpers.WithClient(context, client => client.DeleteAsync($"backups/{id}{suffix}"));
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

    private static string FormatProgress(BackupDto backup)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Backup {backup.Id} {backup.Status} tables={backup.TableCount} size={FormatBytes(backup.BackupSizeBytes ?? CalculateBackupSizeBytes(backup.Tables))}");
        if (backup.ContentMode == BackupContentMode.SchemaOnly)
        {
            builder.AppendLine($"  schema-only backup captured {backup.TableCount} table schema{(backup.TableCount == 1 ? "" : "s")}; shard data backup was skipped.");
            return builder.ToString().TrimEnd();
        }

        if (backup.Tables.Count == 0)
        {
            builder.AppendLine("  table details are not available in this response.");
            return builder.ToString().TrimEnd();
        }

        foreach (var table in backup.Tables.OrderBy(x => x.Database).ThenBy(x => x.Table))
        {
            builder.AppendLine($"  {table.Database}.{table.Table}  {table.Status}  size={FormatBytes(table.BackupSizeBytes ?? CalculateTableSizeBytes(table))}  {FormatShardProgress(table.Shards)}");
            foreach (var shard in table.Shards.OrderBy(x => x.SourceShardNumber).ThenBy(x => x.ReplicaNumber))
            {
                builder.AppendLine($"    shard {shard.SourceShardNumber}{(string.IsNullOrWhiteSpace(shard.SourceShardName) ? "" : $" ({shard.SourceShardName})")} replica={shard.ReplicaNumber} node={shard.Host}:{shard.Port} status={shard.Status} size={FormatBytes(shard.BackupSizeBytes)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static long? CalculateBackupSizeBytes(IReadOnlyList<BackupTableDto> tables)
    {
        var sizes = tables.Select(x => x.BackupSizeBytes ?? CalculateTableSizeBytes(x)).Where(x => x.HasValue).ToList();
        return sizes.Count == 0 ? null : sizes.Sum(x => x!.Value);
    }

    private static long? CalculateTableSizeBytes(BackupTableDto table)
    {
        var sizes = table.Shards.Select(x => x.BackupSizeBytes).Where(x => x.HasValue).ToList();
        return sizes.Count == 0 ? null : sizes.Sum(x => x!.Value);
    }

    private static string FormatBytes(long? value)
    {
        if (value is null) return "unknown";
        if (value == 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var amount = (double)value.Value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return unit == 0 || amount >= 10 ? $"{amount:0} {units[unit]}" : $"{amount:0.0} {units[unit]}";
    }

    private static string FormatShardProgress(IReadOnlyList<BackupTableShardDto> shards)
    {
        if (shards.Count == 0)
        {
            return "shards=0";
        }

        if (shards.Count == 1)
        {
            return $"shard={shards[0].Status}";
        }

        var queued = shards.Count(x => x.Status == BackupTableStatus.Queued);
        var running = shards.Count(x => x.Status == BackupTableStatus.Running);
        var succeeded = shards.Count(x => x.Status == BackupTableStatus.Succeeded);
        var failed = shards.Count(x => x.Status == BackupTableStatus.Failed);
        var skipped = shards.Count(x => x.Status == BackupTableStatus.Skipped);
        var completed = succeeded + failed + skipped;
        return $"shards={shards.Count} queued={queued} running={running} completed={completed} succeeded={succeeded} failed={failed} skipped={skipped}";
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

