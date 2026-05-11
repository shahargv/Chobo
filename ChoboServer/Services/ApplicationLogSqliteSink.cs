using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog.Core;
using Serilog.Events;

namespace ChoboServer.Services;

public sealed class ApplicationLogSqliteSink(string dataDirectory) : ILogEventSink
{
    private readonly string _dbPath = Path.Combine(dataDirectory, "chobo.db");

    public void Emit(LogEvent logEvent)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                DefaultTimeout = 60
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, Properties)
                                  VALUES ($timestamp, $level, $exception, $message, $properties);
                                  """;
            command.Parameters.AddWithValue("$timestamp", logEvent.Timestamp.ToUniversalTime().ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$level", logEvent.Level.ToString());
            command.Parameters.AddWithValue("$exception", (object?)logEvent.Exception?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", logEvent.RenderMessage());
            command.Parameters.AddWithValue("$properties", SerializeProperties(logEvent));
            command.ExecuteNonQuery();
        }
        catch
        {
            // Logging must never break the application path.
        }
    }

    private static string SerializeProperties(LogEvent logEvent)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var property in logEvent.Properties)
        {
            properties[property.Key] = ToJsonValue(property.Value);
        }

        return JsonSerializer.Serialize(properties, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static object? ToJsonValue(LogEventPropertyValue value) =>
        value switch
        {
            ScalarValue scalar => scalar.Value,
            SequenceValue sequence => sequence.Elements.Select(ToJsonValue).ToList(),
            StructureValue structure => structure.Properties.ToDictionary(x => x.Name, x => ToJsonValue(x.Value)),
            DictionaryValue dictionary => dictionary.Elements.ToDictionary(x => x.Key.Value?.ToString() ?? "", x => ToJsonValue(x.Value)),
            _ => value.ToString()
        };
}
