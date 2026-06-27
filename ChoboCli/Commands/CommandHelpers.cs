using System.Text.Json;
using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

internal static class CommandHelpers
{
    public static async Task<object?> WithClient(CommandContext context, Func<Infrastructure.ChoboApiClient, Task<object?>> action)
    {
        using var client = await context.CreateClientAsync();
        return await action(client);
    }

    public static string QueryString(OptionBag options)
    {
        var query = new List<string>();
        if (options.Optional("--last") is { } last) query.Add($"last={Uri.EscapeDataString(last)}");
        if (options.Optional("--start-time") is { } start) query.Add($"startTime={Uri.EscapeDataString(start)}");
        if (options.Optional("--end-time") is { } end) query.Add($"endTime={Uri.EscapeDataString(end)}");
        if (options.Optional("--offset") is { } offset) query.Add($"offset={Uri.EscapeDataString(offset)}");
        if (options.Optional("--limit") is { } limit) query.Add($"limit={Uri.EscapeDataString(limit)}");
        if (options.Optional("--operation-id") is { } operationId) query.Add($"operationId={Uri.EscapeDataString(operationId)}");
        if (options.Optional("--severity") is { } severity) query.Add($"severity={Uri.EscapeDataString(severity)}");
        return query.Count == 0 ? "" : "?" + string.Join("&", query);
    }

    public static PolicySelector PolicySelectorFromOption(OptionBag options) =>
        options.Optional("--selector-file") is { } path
            ? JsonSerializer.Deserialize<PolicySelector>(File.ReadAllText(path), Infrastructure.JsonOutputWriter.JsonOptions) ?? PolicySelector.Empty
            : PolicySelector.Empty;

    public static PolicyEvaluationRequest PolicyEvaluationRequestFromOption(OptionBag options) =>
        options.Optional("--inventory-file") is { } path
            ? new PolicyEvaluationRequest(JsonSerializer.Deserialize<PolicyInventory>(File.ReadAllText(path), Infrastructure.JsonOutputWriter.JsonOptions) ?? new PolicyInventory([]))
            : new PolicyEvaluationRequest(new PolicyInventory([]));

    public static IReadOnlyDictionary<string, JsonElement>? ClickHouseSettingsFromOptions(OptionBag options, string prefix, IReadOnlyDictionary<string, JsonElement>? inherited = null)
    {
        var jsonOption = $"--clickhouse-{prefix}-settings-json";
        var fileOption = $"--clickhouse-{prefix}-settings-file";
        var settingOption = $"--clickhouse-{prefix}-setting";
        var removeOption = $"--remove-clickhouse-{prefix}-setting";
        var hasAny = options.Has(jsonOption) || options.Has(fileOption) || options.Has(settingOption) || options.Has(removeOption);
        if (!hasAny)
        {
            return null;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in inherited ?? new Dictionary<string, JsonElement>())
        {
            result[name] = value.Clone();
        }
        if (options.Optional(fileOption) is { } filePath) MergeSettingsJson(result, File.ReadAllText(filePath));
        if (options.Optional(jsonOption) is { } json) MergeSettingsJson(result, json);
        foreach (var item in options.Values(settingOption))
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                throw new InvalidOperationException($"{settingOption} values must use name=value syntax.");
            }

            result[parts[0]] = ParseSettingValue(parts[1]);
        }
        foreach (var name in options.Values(removeOption))
        {
            result.Remove(name);
        }

        return result;
    }

    private static void MergeSettingsJson(Dictionary<string, JsonElement> target, string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Infrastructure.JsonOutputWriter.JsonOptions)
            ?? throw new InvalidOperationException("ClickHouse settings JSON must be an object.");
        foreach (var (name, value) in values)
        {
            target[name] = value.Clone();
        }
    }

    private static JsonElement ParseSettingValue(string raw)
    {
        if (bool.TryParse(raw, out var boolean))
        {
            return JsonSerializer.SerializeToElement(boolean, Infrastructure.JsonOutputWriter.JsonOptions);
        }
        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return JsonSerializer.SerializeToElement(number, Infrastructure.JsonOutputWriter.JsonOptions);
        }

        return JsonSerializer.SerializeToElement(raw, Infrastructure.JsonOutputWriter.JsonOptions);
    }
}
