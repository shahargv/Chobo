using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChoboServer.Services;

public static class AuditDetails
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static AuditDetails()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static object Change(object? previous, object? current) =>
        new { previous, current };

    public static object Deactivation(object? deactivated, object? current) =>
        new { deactivated, current };

    public static JsonElement ToJsonElement(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return Empty();
        }

        try
        {
            using var document = JsonDocument.Parse(details);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    public static string ToJsonString(JsonElement details) =>
        details.GetRawText();

    public static string Serialize(object? details) =>
        details is null ? "{}" : JsonSerializer.Serialize(details, JsonOptions);

    public static string? TryGetOperationId(object? details)
    {
        var json = Serialize(details);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("operationId", out var operationId))
            {
                return null;
            }

            return operationId.ValueKind == JsonValueKind.String
                ? operationId.GetString()
                : operationId.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeWithOperationId(object? details, string? operationId)
    {
        var json = Serialize(details);
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return json;
        }

        try
        {
            var node = JsonNode.Parse(json) as JsonObject ?? [];
            node["operationId"] = operationId;
            return node.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { operationId, details = json }, JsonOptions);
        }
    }

    private static JsonElement Empty()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}



