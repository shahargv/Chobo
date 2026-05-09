namespace ChoboCli.Cli;

public sealed class ParsedCommand
{
    private ParsedCommand(string subject, string verb, OptionBag options, bool isHelp)
    {
        Subject = subject;
        Verb = verb;
        Options = options;
        IsHelp = isHelp;
    }

    public string Subject { get; }
    public string Verb { get; }
    public OptionBag Options { get; }
    public bool IsHelp { get; }

    public static ParsedCommand Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(x => x is "--help" or "-h"))
        {
            return new ParsedCommand("", "", OptionBag.Empty, isHelp: true);
        }

        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(current);
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[current] = args[++i];
            }
            else
            {
                options[current] = "true";
            }
        }

        if (positionals.Count < 2)
        {
            throw new InvalidOperationException("Command must include subject and verb. Use --help for examples.");
        }

        return new ParsedCommand(positionals[0], positionals[1], new OptionBag(options), isHelp: false);
    }
}

