using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public interface IAuditStore
{
    Task<PagedResultDto<AuditEntryDto>> QueryAsync(DateTimeOffset? startTime, DateTimeOffset? endTime, int? last, int? offset = null, int? limit = null, string? operationId = null);
    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}

public sealed class AuditStore(ChoboDbContext db) : IAuditStore
{
    public async Task<PagedResultDto<AuditEntryDto>> QueryAsync(DateTimeOffset? startTime, DateTimeOffset? endTime, int? last, int? offset = null, int? limit = null, string? operationId = null)
    {
        var filter = BuildFilter(startTime, endTime, operationId);
        var pageOffset = Math.Max(offset ?? 0, 0);
        var pageLimit = Math.Clamp(limit ?? last ?? 200, 1, 10_000);
        var results = new List<AuditEntryDto>();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var _ = await OpenIfNeededAsync(connection);
        var totalCount = await CountAsync(connection, filter);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                              SELECT Id, Timestamp, ActorUserId, ActorName, Action, EntityType, EntityId, Details
                              FROM AuditEntries
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
            results.Add(new AuditEntryDto(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                AuditDetails.ToJsonElement(reader.GetString(7))));
        }

        return new PagedResultDto<AuditEntryDto>(results, pageOffset, pageLimit, totalCount);
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var _ = await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AuditEntries WHERE Timestamp < $before;";
        command.Parameters.AddWithValue("$before", ToSqlValue(before));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(SqliteConnection connection, QueryFilter filter)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM AuditEntries {filter.WhereSql};";
        AddParameters(command, filter);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static QueryFilter BuildFilter(DateTimeOffset? startTime, DateTimeOffset? endTime, string? operationId)
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

        return new QueryFilter(predicates.Count == 0 ? "" : "WHERE " + string.Join(" AND ", predicates), parameters);
    }

    private static void AddParameters(SqliteCommand command, QueryFilter filter)
    {
        foreach (var parameter in filter.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static object ToSqlValue(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToUniversalTime().ToUnixTimeMilliseconds();

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
