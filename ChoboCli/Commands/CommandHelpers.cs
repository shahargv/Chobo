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
        if (options.Optional("--last") is { } last)
        {
            query.Add($"last={Uri.EscapeDataString(last)}");
        }
        if (options.Optional("--start-time") is { } start)
        {
            query.Add($"startTime={Uri.EscapeDataString(start)}");
        }
        if (options.Optional("--end-time") is { } end)
        {
            query.Add($"endTime={Uri.EscapeDataString(end)}");
        }

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
}
