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
        CommandHelpers.WithClient(context, client => client.GetAsync("policies"));

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
        var required = options.Require("--name", "--source-cluster-id", "--target-id");
        return new UpsertPolicyRequest(
            required["--name"],
            Guid.Parse(required["--source-cluster-id"]),
            Guid.Parse(required["--target-id"]),
            CommandHelpers.PolicySelectorFromOption(options));
    }
}
