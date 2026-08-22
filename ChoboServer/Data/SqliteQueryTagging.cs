using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ChoboServer.Data;

public static partial class SqliteQueryTagging
{
    public const string MarkerPrefix = "-- chobo-query-tag: ";
    private const int MaxTagLength = 160;
    private static readonly AsyncLocal<string?> CurrentTag = new();

    public static IDisposable Push(string tag)
    {
        Validate(tag);
        var previous = CurrentTag.Value;
        CurrentTag.Value = tag;
        return new TagScope(previous);
    }

    public static SqliteCommandTag EnsureTag(DbCommand command)
    {
        var existing = TryExtract(command.CommandText);
        if (existing is not null)
        {
            return new SqliteCommandTag(existing, existing.StartsWith("sqlite.unattributed.", StringComparison.Ordinal));
        }

        var tag = CurrentTag.Value;
        var isMissing = false;
        if (tag is null)
        {
            tag = CreateAutomaticTag(command.CommandText);
            isMissing = tag.StartsWith("sqlite.unattributed.", StringComparison.Ordinal);
        }

        command.CommandText = $"{MarkerPrefix}{tag}{Environment.NewLine}{command.CommandText}";
        return new SqliteCommandTag(tag, isMissing);
    }

    public static string? TryExtract(string commandText)
    {
        var match = TagMarkerRegex().Match(commandText);
        return match.Success ? match.Groups["tag"].Value : null;
    }

    private static string CreateAutomaticTag(string commandText)
    {
        var operation = ExtractOperation(commandText);
        var table = ExtractTable(commandText, operation);
        var caller = FindChoboCaller();
        var tag = caller is null
            ? $"sqlite.unattributed.{operation}.{table}"
            : $"{caller.Value.Type}.{caller.Value.Method}.{operation}.{table}";
        return tag.Length <= MaxTagLength ? tag : tag[..MaxTagLength].TrimEnd('-', '.');
    }

    private static (string Type, string Method)? FindChoboCaller()
    {
        foreach (var frame in new StackTrace(1, false).GetFrames())
        {
            var method = ResolveAsyncMethod(frame.GetMethod());
            var type = NormalizeDeclaringType(method?.DeclaringType);
            if (method is null || type is null || type.Assembly != typeof(SqliteQueryTagging).Assembly)
            {
                continue;
            }

            if (type == typeof(SqliteQueryTagging) || type == typeof(SlowSqliteQueryLoggingInterceptor) || type == typeof(ChoboDbContext))
            {
                continue;
            }

            return (ToKebabCase(TrimTypeSuffix(type.Name)), ToKebabCase(TrimMethodSuffix(NormalizeMethodName(method.Name))));
        }

        return null;
    }

    private static MethodBase? ResolveAsyncMethod(MethodBase? method)
    {
        var stateMachineType = method?.DeclaringType;
        var parentType = stateMachineType?.DeclaringType;
        if (method?.Name != nameof(IAsyncStateMachine.MoveNext) || stateMachineType is null || parentType is null)
        {
            return method;
        }

        return parentType
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType == stateMachineType)
            ?? method;
    }

    private static Type? NormalizeDeclaringType(Type? type)
    {
        while (type?.DeclaringType is not null &&
               (type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null || type.Name.StartsWith('<')))
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static string NormalizeMethodName(string value)
    {
        if (!value.StartsWith('<'))
        {
            return value;
        }

        var close = value.IndexOf('>');
        return close > 1 ? value[1..close] : value;
    }

    private static string ExtractOperation(string commandText)
    {
        var match = OperationRegex().Match(commandText);
        return match.Success ? match.Groups["operation"].Value.ToLowerInvariant() : "command";
    }

    private static string ExtractTable(string commandText, string operation)
    {
        var regex = operation switch
        {
            "select" => SelectTableRegex(),
            "insert" => InsertTableRegex(),
            "update" => UpdateTableRegex(),
            "delete" => DeleteTableRegex(),
            "alter" => AlterTableRegex(),
            "create" => CreateTableRegex(),
            _ => null
        };
        var match = regex?.Match(commandText);
        return match is { Success: true } ? ToKebabCase(match.Groups["table"].Value) : "database";
    }

    private static string TrimTypeSuffix(string value)
    {
        foreach (var suffix in new[] { "ApplicationService", "BackgroundService", "Repository", "Service", "Store", "Controller" })
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }

    private static string TrimMethodSuffix(string value) =>
        value.EndsWith("Async", StringComparison.Ordinal) && value.Length > "Async".Length
            ? value[..^"Async".Length]
            : value;

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsLetterOrDigit(ch))
            {
                if (char.IsUpper(ch) && builder.Length > 0 && builder[^1] != '-' &&
                    (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('-');
                }
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static void Validate(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength || !ValidTagRegex().IsMatch(tag))
        {
            throw new ArgumentException("SQLite query tag must be a lowercase dot-separated name containing only letters, numbers, and hyphens.", nameof(tag));
        }
    }

    private sealed class TagScope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentTag.Value = previous;
    }

    [GeneratedRegex(@"(?m)^\s*--\s*chobo-query-tag:\s*(?<tag>[a-z0-9]+(?:[.-][a-z0-9]+)*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TagMarkerRegex();

    [GeneratedRegex(@"(?im)^\s*(?:(?:--[^\r\n]*)(?:\r?\n|$)\s*)*(?<operation>select|insert|update|delete|alter|create|drop|pragma|with|replace)\b", RegexOptions.CultureInvariant)]
    private static partial Regex OperationRegex();

    [GeneratedRegex("""\bFROM\s+["`\[]?(?<table>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectTableRegex();

    [GeneratedRegex("""\bINTO\s+["`\[]?(?<table>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertTableRegex();

    [GeneratedRegex("""\bUPDATE\s+["`\[]?(?<table>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateTableRegex();

    [GeneratedRegex("""\bDELETE\s+FROM\s+["`\[]?(?<table>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteTableRegex();

    [GeneratedRegex("""\bALTER\s+TABLE\s+["`\[]?(?<table>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlterTableRegex();

    [GeneratedRegex("""\bCREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+["`\[]?(?<table>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableRegex();

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidTagRegex();
}

public sealed record SqliteCommandTag(string Name, bool IsMissing);
