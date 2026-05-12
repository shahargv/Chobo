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
        Verb("remove", "Soft-delete a ClickHouse cluster.", RemoveAsync);
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

    private static Task<object?> RemoveAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"clusters/{context.Command.Options.Required("--id")}"));

    private static UpsertClusterRequest Request(OptionBag options)
    {
        var required = options.Require("--name");
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
            options.Optional("--backup-restore-maxdop") is { } maxDop ? int.Parse(maxDop) : null,
            options.Optional("--clickhouse-cluster-name") ?? options.Optional("--cluster-name-in-clickhouse"));
    }
}
