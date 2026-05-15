using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public sealed class AuditStore(ChoboDbContext db)
{
    public async Task<IReadOnlyList<AuditEntryDto>> QueryAsync(DateTimeOffset? startTime, DateTimeOffset? endTime, int? last)
    {
        const string sql = """
                           SELECT Id, Timestamp, ActorUserId, ActorName, Action, EntityType, EntityId, Details
                           FROM AuditEntries
                           WHERE ($startTime IS NULL OR Timestamp >= $startTime)
                             AND ($endTime IS NULL OR Timestamp <= $endTime)
                           ORDER BY Timestamp DESC
                           LIMIT $limit;
                           """;

        var results = new List<AuditEntryDto>();
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

        return results;
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
