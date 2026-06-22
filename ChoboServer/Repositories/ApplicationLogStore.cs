using System.Text;
using System.Text.Json;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public interface IApplicationLogStore
{
    Task<PagedResultDto<ApplicationLogEntryDto>> QueryAsync(DateTimeOffset? startTime, DateTimeOffset? endTime, int? last, int? offset = null, int? limit = null, string? operationId = null, string? severity = null);
    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}

public sealed class ApplicationLogStore(ChoboDbContext db) : IApplicationLogStore
{
    public async Task<PagedResultDto<ApplicationLogEntryDto>> QueryAsync(DateTimeOffset? startTime, DateTimeOffset? endTime, int? last, int? offset = null, int? limit = null, string? operationId = null, string? severity = null)
    {
        var filter = BuildFilter(startTime, endTime, operationId, severity);
        var pageOffset = Math.Max(offset ?? 0, 0);
        var pageLimit = Math.Clamp(limit ?? last ?? 200, 1, 10_000);
        var results = new List<ApplicationLogEntryDto>();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var _ = await OpenIfNeededAsync(connection);
        var totalCount = await CountAsync(connection, filter);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                              SELECT Id, Timestamp, Level, RenderedMessage, Exception, Properties
                              FROM ApplicationLogEntries
                              {filter.WhereSql}
                              ORDER BY Timestamp DESC, Id DESC
                              LIMIT $limit OFFSET $offset;
                              """;
        AddParameters(command, filter);
        command.Parameters.AddWithValue("$limit", pageLimit);
        command.Parameters.AddWithValue("$offset", pageOffset);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ApplicationLogEntryDto(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2),
                ExtractSourceContext(reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new PagedResultDto<ApplicationLogEntryDto>(results, pageOffset, pageLimit, totalCount);
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var _ = await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ApplicationLogEntries WHERE Timestamp < $before;";
        command.Parameters.AddWithValue("$before", ToSqlValue(before));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(SqliteConnection connection, QueryFilter filter)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ApplicationLogEntries {filter.WhereSql};";
        AddParameters(command, filter);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static QueryFilter BuildFilter(DateTimeOffset? startTime, DateTimeOffset? endTime, string? operationId, string? severity)
    {
        var predicates = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        if (startTime is not null)
        {
            predicates.Add("Timestamp >= $startTime");
            parameters.Add(("$startTime", ToSqlValue(startTime)));
        }
        if (endTime is not null)
        {
            predicates.Add("Timestamp <= $endTime");
            parameters.Add(("$endTime", ToSqlValue(endTime)));
        }
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            predicates.Add("OperationId = $operationId");
            parameters.Add(("$operationId", operationId));
        }
        var severityLevels = ParseSeverityLevels(severity);
        if (severityLevels.Count > 0)
        {
            var placeholders = new List<string>();
            for (var i = 0; i < severityLevels.Count; i++)
            {
                var parameterName = $"$level{i}";
                placeholders.Add(parameterName);
                parameters.Add((parameterName, severityLevels[i]));
            }
            predicates.Add("Level IN (" + string.Join(", ", placeholders) + ")");
        }

        return new QueryFilter(predicates.Count == 0 ? "" : "WHERE " + string.Join(" AND ", predicates), parameters);
    }

    private static IReadOnlyList<string> ParseSeverityLevels(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
        {
            return [];
        }

        return severity
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSeverityLevel)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? NormalizeSeverityLevel(string value) =>
        value.ToLowerInvariant() switch
        {
            "verbose" => "Verbose",
            "debug" => "Debug",
            "information" or "info" => "Information",
            "warning" or "warn" => "Warning",
            "error" => "Error",
            "fatal" => "Fatal",
            _ => null
        };
    private static void AddParameters(SqliteCommand command, QueryFilter filter)
    {
        foreach (var parameter in filter.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static object ToSqlValue(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToUniversalTime().ToUnixTimeMilliseconds();

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

    private sealed record QueryFilter(string WhereSql, IReadOnlyList<(string Name, object Value)> Parameters);

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


