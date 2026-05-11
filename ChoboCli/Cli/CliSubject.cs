namespace ChoboCli.Cli;

public abstract class CliSubject : ICliSubject
{
    private readonly Dictionary<string, ICommandHandler> _verbs = new(StringComparer.OrdinalIgnoreCase);

    public abstract string Name { get; }
    public abstract string Description { get; }
    public IReadOnlyCollection<ICommandHandler> Verbs => _verbs.Values;

    public ICommandHandler Resolve(string verbName)
    {
        if (_verbs.TryGetValue(verbName, out var handler))
        {
            return handler;
        }

        throw new InvalidOperationException($"Unknown verb '{verbName}' for subject '{Name}'. Available: {string.Join(", ", _verbs.Keys.OrderBy(x => x))}.");
    }

    protected void Verb(string name, string description, Func<CommandContext, Task<object?>> handler) =>
        _verbs.Add(name, new CommandHandler(name, description, handler));
}

public interface ICliSubject
{
    string Name { get; }
    string Description { get; }
    IReadOnlyCollection<ICommandHandler> Verbs { get; }
    ICommandHandler Resolve(string verbName);
}

