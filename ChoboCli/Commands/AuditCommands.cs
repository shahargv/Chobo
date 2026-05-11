using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class AuditCommands : CliSubject
{
    public AuditCommands()
    {
        Verb("show", "Show audit records.", ShowAsync);
        Verb("clear", "Clear audit records before a timestamp.", ClearAsync);
    }

    public override string Name => "audit";
    public override string Description => "Audit record queries.";

    private static Task<object?> ShowAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("audit" + CommandHelpers.QueryString(context.Command.Options)));

    private static Task<object?> ClearAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("audit/clear", new ClearBeforeRequest(DateTimeOffset.Parse(context.Command.Options.Required("--before")))));
}
