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
        const string whereSql = """
                                WHERE ($startTime IS NULL OR Timestamp >= $startTime)
                                  AND ($endTime IS NULL OR Timestamp <= $endTime)
                                  AND ($operationId IS NULL OR OperationId = $operationId)
                                """;
        var pageOffset = Math.Max(offset ?? 0, 0);
        var pageLimit = Math.Clamp(limit ?? last ?? 200, 1, 10_000);
        var results = new List<AuditEntryDto>();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var _ = await OpenIfNeededAsync(connection);
        var totalCount = await CountAsync(connection, whereSql, startTime, endTime, operationId);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                              SELECT Id, Timestamp, ActorUserId, ActorName, Action, EntityType, EntityId, Details
                              FROM AuditEntries
                              {whereSql}
                              ORDER BY Timestamp DESC, Id DESC
                              LIMIT $limit OFFSET $offset;
                              """;
        AddFilterParameters(command, startTime, endTime, operationId);
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

    private static async Task<int> CountAsync(SqliteConnection connection, string whereSql, DateTimeOffset? startTime, DateTimeOffset? endTime, string? operationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM AuditEntries {whereSql};";
        AddFilterParameters(command, startTime, endTime, operationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static void AddFilterParameters(SqliteCommand command, DateTimeOffset? startTime, DateTimeOffset? endTime, string? operationId)
    {
        command.Parameters.AddWithValue("$startTime", ToSqlValue(startTime));
        command.Parameters.AddWithValue("$endTime", ToSqlValue(endTime));
        command.Parameters.AddWithValue("$operationId", string.IsNullOrWhiteSpace(operationId) ? DBNull.Value : operationId);
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

