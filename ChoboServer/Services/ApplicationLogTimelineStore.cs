using System.Text.Json;
using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Services;

public sealed class ApplicationLogTimelineStore(ChoboDbContext db)
{
    public async Task<IReadOnlyList<LogEntryDto>> QueryAsync(DateTimeOffset? startTime, DateTimeOffset? endTime, int? last)
    {
        const string sql = """
                           SELECT Id, Timestamp, Level, RenderedMessage, Exception, Properties
                           FROM ApplicationLogEntries
                           WHERE ($startTime IS NULL OR Timestamp >= $startTime)
                             AND ($endTime IS NULL OR Timestamp <= $endTime)
                           ORDER BY Timestamp DESC
                           LIMIT $limit;
                           """;

        var results = new List<LogEntryDto>();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var _ = await OpenIfNeededAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$startTime", ToSqlValue(startTime));
        command.Parameters.AddWithValue("$endTime", ToSqlValue(endTime));
        command.Parameters.AddWithValue("$limit", Math.Clamp(last ?? 500, 1, 10_000));

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new LogEntryDto(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                ExtractSourceContext(reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var _ = await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ApplicationLogEntries WHERE Timestamp < $before;";
        command.Parameters.AddWithValue("$before", ToSqlText(before));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object ToSqlValue(DateTimeOffset? value) =>
        value is null ? DBNull.Value : ToSqlText(value.Value);

    private static string ToSqlText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");

    private static string ExtractSourceContext(string? propertiesJson)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(propertiesJson);
            if (!document.RootElement.TryGetProperty("SourceContext", out var sourceContext))
            {
                return "";
            }

            return sourceContext.ValueKind == JsonValueKind.String
                ? sourceContext.GetString() ?? ""
                : sourceContext.ToString();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static async Task<IAsyncDisposable> OpenIfNeededAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection.State == System.Data.ConnectionState.Open)
        {
            return new NoopAsyncDisposable();
        }

        await connection.OpenAsync(cancellationToken);
        return new ConnectionCloseScope(connection);
    }

    private sealed class ConnectionCloseScope(SqliteConnection connection) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            connection.Close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
