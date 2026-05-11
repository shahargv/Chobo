namespace ChoboCli.Cli;

public sealed class OptionBag(Dictionary<string, string?> values)
{
    public static OptionBag Empty { get; } = new(new Dictionary<string, string?>());

    public string Required(string name) =>
        Optional(name) ?? throw new InvalidOperationException($"{name} is required.");

    public IReadOnlyDictionary<string, string> Require(params string[] names)
    {
        var missing = names.Where(name => string.IsNullOrWhiteSpace(Optional(name))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing required options: {string.Join(", ", missing)}.");
        }

        return names.ToDictionary(name => name, name => Optional(name)!);
    }

    public string? Optional(string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    public bool Has(string name) =>
        values.ContainsKey(name);

    public int Int(string name, int defaultValue) =>
        Optional(name) is { } value ? int.Parse(value) : defaultValue;

    public T Enum<T>(string name, T defaultValue) where T : struct =>
        Optional(name) is { } value ? System.Enum.Parse<T>(value, ignoreCase: true) : defaultValue;
}
