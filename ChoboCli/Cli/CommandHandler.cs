namespace ChoboCli.Cli;

public sealed record CommandHandler(string Name, string Description, Func<CommandContext, Task<object?>> Handler) : ICommandHandler
{
    public Task<object?> HandleAsync(CommandContext context) => Handler(context);
}

public interface ICommandHandler
{
    string Name { get; }
    string Description { get; }
    Task<object?> HandleAsync(CommandContext context);
}

