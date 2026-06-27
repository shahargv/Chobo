using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class ClusterCommands : CliSubject
{
    public ClusterCommands()
    {
        Verb("list", "List ClickHouse clusters.", ListAsync);
        Verb("add", "Add a ClickHouse cluster.", AddAsync);
        Verb("update", "Update a ClickHouse cluster.", UpdateAsync);
        Verb("update-credentials", "Update ClickHouse cluster credentials.", UpdateCredentialsAsync);
        Verb("remove", "Soft-delete a ClickHouse cluster.", RemoveAsync);
        Verb("test-connection", "Test a ClickHouse cluster connection.", TestConnectionAsync);
    }

    public override string Name => "clusters";
    public override string Description => "ClickHouse cluster configuration.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("clusters"));

    private static Task<object?> AddAsync(CommandContext context)
    {
        var request = Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PostAsync("clusters", request));
    }

    private static Task<object?> UpdateAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--id");
        var request = Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PutAsync($"clusters/{required["--id"]}", request));
    }

    private static Task<object?> UpdateCredentialsAsync(CommandContext context)
    {
        var id = context.Command.Options.Required("--id");
        if (context.Command.Options.Optional("--username") is null && context.Command.Options.Optional("--password") is null)
        {
            throw new InvalidOperationException("Specify --username, --password, or both.");
        }

        var request = new UpdateClusterCredentialsRequest(
            context.Command.Options.Optional("--username"),
            context.Command.Options.Optional("--password"));
        return CommandHelpers.WithClient(context, client => client.PostAsync($"clusters/{id}/credentials", request));
    }

    private static Task<object?> RemoveAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"clusters/{context.Command.Options.Required("--id")}"));

    private static Task<object?> TestConnectionAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"clusters/{context.Command.Options.Required("--id")}/test-connection", new { }));

    private static UpsertClusterRequest Request(OptionBag options)
    {
        var required = options.Require("--name", "--backup-restore-maxdop");
        if (options.Optional("--node") is null && options.Optional("--host") is null)
        {
            throw new InvalidOperationException("Missing required options: --host or --node.");
        }

        var nodes = (options.Optional("--node") ?? options.Optional("--host")!)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value =>
            {
                var parts = value.Split(':');
                return new UpsertAccessNodeRequest(
                    parts[0],
                    parts.Length > 1 ? int.Parse(parts[1]) : options.Int("--port", 9000),
                    options.Has("--tls"));
            })
            .ToList();

        return new UpsertClusterRequest(
            required["--name"],
            options.Enum("--mode", ClusterMode.SingleInstance),
            nodes,
            options.Optional("--username"),
            options.Optional("--password"),
            int.Parse(required["--backup-restore-maxdop"]),
            options.Optional("--clickhouse-cluster-name") ?? options.Optional("--cluster-name-in-clickhouse"),
            options.Int("--node-maxdop", 1),
            ParseNodeOverrides(options.Optional("--node-maxdop-overrides")),
            options.Int("--shard-maxdop", 1),
            ParseShardOverrides(options.Optional("--shard-maxdop-overrides")),
            CommandHelpers.ClickHouseSettingsFromOptions(options, "backup"),
            CommandHelpers.ClickHouseSettingsFromOptions(options, "restore"));
    }

    private static IReadOnlyList<ClusterNodeMaxDopOverrideDto>? ParseNodeOverrides(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item =>
                {
                    var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
                    var endpoint = parts[0].Split(':', StringSplitOptions.TrimEntries);
                    return new ClusterNodeMaxDopOverrideDto(endpoint[0], endpoint.Length > 1 ? int.Parse(endpoint[1]) : 9000, false, int.Parse(parts[1]));
                })
                .ToList();

    private static IReadOnlyList<ClusterShardMaxDopOverrideDto>? ParseShardOverrides(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item =>
                {
                    var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
                    var shardNumber = int.Parse(parts[0]);
                    return new ClusterShardMaxDopOverrideDto(shardNumber, null, int.Parse(parts[1]));
                })
                .ToList();
}
