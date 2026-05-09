using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoboCli.Infrastructure;

public sealed class JsonOutputWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public void Write(object? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is string text)
        {
            WriteText(text);
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    public void WriteText(string value) =>
        Console.WriteLine(value);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
