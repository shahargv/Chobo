using System.Text.Encodings.Web;
using System.Text.Json;

namespace ChoboServer.Services;

public static class AuditDetails
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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

    private static JsonElement Empty()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
