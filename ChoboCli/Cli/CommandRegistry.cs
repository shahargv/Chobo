using System.Text;

namespace ChoboCli.Cli;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICliSubject> _subjects = new(StringComparer.OrdinalIgnoreCase);

    public CommandRegistry Add(ICliSubject subject)
    {
        _subjects.Add(subject.Name, subject);
        return this;
    }

    public ICommandHandler Resolve(string subjectName, string verbName)
    {
        if (!_subjects.TryGetValue(subjectName, out var subject))
        {
            throw new InvalidOperationException($"Unknown subject '{subjectName}'.\n\n{GetHelp()}");
        }

        return subject.Resolve(verbName);
    }

    public string GetHelp()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Use ChoboCli <subject> <verb> [options]");
        builder.AppendLine();
        builder.AppendLine("Subjects:");
        foreach (var subject in _subjects.Values.OrderBy(x => x.Name))
        {
            builder.AppendLine($"  {subject.Name,-10} {subject.Description}");
            foreach (var verb in subject.Verbs.OrderBy(x => x.Name))
            {
                builder.AppendLine($"    {verb.Name,-10} {verb.Description}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Global options: --server-url <url>, --access-token <token>, --help");
        return builder.ToString();
    }
}

