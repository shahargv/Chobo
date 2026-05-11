using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class LogCommands : CliSubject
{
    public LogCommands()
    {
        Verb("show", "Show application logs.", ShowAsync);
        Verb("clear", "Clear application logs before a timestamp.", ClearAsync);
    }

    public override string Name => "logs";
    public override string Description => "Application log queries and maintenance.";

    private static Task<object?> ShowAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("logs" + CommandHelpers.QueryString(context.Command.Options)));

    private static Task<object?> ClearAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("logs/clear", new ClearBeforeRequest(DateTimeOffset.Parse(context.Command.Options.Required("--before")))));
}

