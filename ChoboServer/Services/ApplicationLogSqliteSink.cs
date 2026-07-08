using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChoboServer.Data;
using ChoboServer.Options;
using Microsoft.Data.Sqlite;
using Serilog.Core;
using Serilog.Events;

namespace ChoboServer.Services;

public sealed class ApplicationLogSqliteSink(string dataDirectory, ChoboSqliteOptions? sqliteOptions = null) : ILogEventSink
{
    private static readonly ConcurrentDictionary<string, byte> EnsuredDatabases = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dbPath = ApplicationLogDatabase.PathForDataDirectory(dataDirectory);
    private readonly ChoboSqliteOptions _sqliteOptions = sqliteOptions ?? new ChoboSqliteOptions();

    public void Emit(LogEvent logEvent)
    {
        try
        {
            if (!File.Exists(_dbPath))
            {
                EnsuredDatabases.TryRemove(_dbPath, out _);
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                DefaultTimeout = SqliteDefaultTimeoutSeconds(_sqliteOptions)
            }.ToString());
            connection.Open();

            if (ShouldEnsureDatabase(connection))
            {
                ApplicationLogDatabase.Ensure(connection, _sqliteOptions);
            }
            else
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = SqlitePragmaConnectionInterceptor.BuildConnectionPragmaSql(_sqliteOptions);
                pragma.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, OperationId, Properties)
                                  VALUES ($timestamp, $level, $exception, $message, $operationId, $properties);
                                  """;
            command.Parameters.AddWithValue("$timestamp", logEvent.Timestamp.ToUniversalTime().ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$level", logEvent.Level.ToString());
            command.Parameters.AddWithValue("$exception", (object?)logEvent.Exception?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", logEvent.RenderMessage());
            command.Parameters.AddWithValue("$operationId", (object?)GetOperationId(logEvent) ?? DBNull.Value);
            command.Parameters.AddWithValue("$properties", SerializeProperties(logEvent));
            command.ExecuteNonQuery();
        }
        catch
        {
            // Logging must never break the application path.
        }
    }

    private static string? GetOperationId(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("OperationId", out var value))
        {
            return null;
        }

        return ToJsonValue(value)?.ToString();
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

    static ApplicationLogSqliteSink()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private static object? ToJsonValue(LogEventPropertyValue value) =>
        value switch
        {
            ScalarValue scalar => scalar.Value,
            SequenceValue sequence => sequence.Elements.Select(ToJsonValue).ToList(),
            StructureValue structure => structure.Properties.ToDictionary(x => x.Name, x => ToJsonValue(x.Value)),
            DictionaryValue dictionary => dictionary.Elements.ToDictionary(x => x.Key.Value?.ToString() ?? "", x => ToJsonValue(x.Value)),
            _ => value.ToString()
        };

    private static int SqliteDefaultTimeoutSeconds(ChoboSqliteOptions options) =>
        options.BusyTimeout.TotalSeconds >= int.MaxValue
            ? int.MaxValue
            : Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds));

    private bool ShouldEnsureDatabase(SqliteConnection connection)
    {
        if (EnsuredDatabases.TryAdd(_dbPath, 0))
        {
            return true;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT 1
                              FROM sqlite_master
                              WHERE type = 'table' AND name = 'ApplicationLogEntries'
                              LIMIT 1;
                              """;
        return command.ExecuteScalar() is null;
    }
}
