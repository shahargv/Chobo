using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chobo.Contracts;

namespace ChoboServer.Services;

public enum ClickHouseAdvancedSettingsKind
{
    Backup,
    Restore
}

public static class ClickHouseAdvancedSettings
{
    private static readonly Regex SettingNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> BackupReserved = new(StringComparer.OrdinalIgnoreCase) { "base_backup", "password", "use_same_password_for_base_backup" };
    private static readonly HashSet<string> RestoreReserved = new(StringComparer.OrdinalIgnoreCase) { "allow_non_empty_tables", "allow_different_table_def", "password", "use_same_password_for_base_backup" };

    public static IReadOnlyDictionary<string, JsonElement> Empty { get; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, JsonElement> Normalize(IReadOnlyDictionary<string, JsonElement>? settings, ClickHouseAdvancedSettingsKind kind)
    {
        if (settings is null || settings.Count == 0)
        {
            return Empty;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, value) in settings)
        {
            var name = NormalizeName(rawName, kind);
            if (result.ContainsKey(name))
            {
                throw new ArgumentException($"ClickHouse setting '{rawName}' is specified more than once.");
            }

            ValidateValue(name, value);
            result[name] = value.Clone();
        }

        return result.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, JsonElement> Deserialize(string? json, ClickHouseAdvancedSettingsKind kind)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
            return Normalize(values, kind);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("ClickHouse advanced settings must be a JSON object containing string, number, or boolean values.", ex);
        }
    }

    public static string Serialize(IReadOnlyDictionary<string, JsonElement>? settings, ClickHouseAdvancedSettingsKind kind) =>
        JsonSerializer.Serialize(Normalize(settings, kind), JsonOptions);

    public static string SerializeNormalized(IReadOnlyDictionary<string, JsonElement>? settings) =>
        JsonSerializer.Serialize(settings ?? Empty, JsonOptions);

    public static ClickHouseSettingsPreviewDto MergeWithSources(params (string Source, IReadOnlyDictionary<string, JsonElement> Settings)[] layers)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, settings) in layers)
        {
            foreach (var (name, value) in settings)
            {
                values[name] = value.Clone();
                sources[name] = source;
            }
        }

        var ordered = values.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        return new ClickHouseSettingsPreviewDto(
            ordered,
            ordered.Select(x => new ClickHouseSettingSourceDto(x.Key, x.Value, sources[x.Key])).ToList());
    }

    public static IReadOnlyDictionary<string, JsonElement> WithPolicyCompression(
        IReadOnlyDictionary<string, JsonElement>? settings,
        BackupCompressionMethod? method,
        int? level)
    {
        var result = new Dictionary<string, JsonElement>(Normalize(settings, ClickHouseAdvancedSettingsKind.Backup), StringComparer.OrdinalIgnoreCase);
        if (method is null)
        {
            return result;
        }

        result["compression_method"] = JsonSerializer.SerializeToElement(method.Value.ToString().ToLowerInvariant());
        if (level is not null)
        {
            result["compression_level"] = JsonSerializer.SerializeToElement(level.Value);
        }
        else
        {
            result.Remove("compression_level");
        }
        return result;
    }

    public static BackupCompressionMethod? CompressionMethod(IReadOnlyDictionary<string, JsonElement>? settings)
    {
        if (settings is null || !settings.TryGetValue("compression_method", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Enum.TryParse<BackupCompressionMethod>(value.GetString(), true, out var method) ? method : null;
    }

    public static int? CompressionLevel(IReadOnlyDictionary<string, JsonElement>? settings) =>
        settings is not null && settings.TryGetValue("compression_level", out var value) && value.TryGetInt32(out var level) ? level : null;

    public static bool RequiresZipArchive(IReadOnlyDictionary<string, JsonElement>? settings) =>
        settings is not null && (settings.ContainsKey("compression_method") || settings.ContainsKey("compression_level"));

    public static string ToSettingsClause(IReadOnlyDictionary<string, JsonElement>? userSettings, params (string Name, string SqlValue)[] choboSettings)
    {
        var parts = new List<string>();
        foreach (var (name, value) in (userSettings ?? Empty).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            parts.Add($"{name} = {RenderValue(value)}");
        }

        foreach (var (name, sqlValue) in choboSettings)
        {
            if (!string.IsNullOrWhiteSpace(sqlValue))
            {
                parts.Add($"{name} = {sqlValue}");
            }
        }

        return parts.Count == 0 ? "" : " SETTINGS " + string.Join(", ", parts);
    }

    private static string NormalizeName(string rawName, ClickHouseAdvancedSettingsKind kind)
    {
        var name = rawName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("ClickHouse setting name is required.");
        }
        if (!SettingNamePattern.IsMatch(name))
        {
            throw new ArgumentException($"ClickHouse setting '{rawName}' is not a valid setting name.");
        }
        if (Reserved(kind).Contains(name))
        {
            throw new ArgumentException($"ClickHouse setting '{name}' is managed by Chobo and cannot be configured.");
        }

        return name.ToLowerInvariant();
    }

    private static HashSet<string> Reserved(ClickHouseAdvancedSettingsKind kind) =>
        kind == ClickHouseAdvancedSettingsKind.Backup ? BackupReserved : RestoreReserved;

    private static void ValidateValue(string name, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        throw new ArgumentException($"ClickHouse setting '{name}' must be a string, number, or boolean value.");
    }

    private static string RenderValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => ClickHouseSql.Literal(value.GetString() ?? ""),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => throw new ArgumentException("ClickHouse setting values must be string, number, or boolean values.")
        };
}
