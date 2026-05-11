using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class MetricsCommands : CliSubject
{
    public MetricsCommands()
    {
        Verb("show", "Show general server metrics.", ShowAsync);
    }

    public override string Name => "metrics";
    public override string Description => "General server metrics.";

    private static Task<object?> ShowAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("metrics"));
}
