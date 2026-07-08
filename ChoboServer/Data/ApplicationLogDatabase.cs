using ChoboServer.Options;
using Microsoft.Data.Sqlite;

namespace ChoboServer.Data;

public static class ApplicationLogDatabase
{
    public const string FileName = "chobo-logs.db";

    public static void Ensure(SqliteConnection connection, ChoboSqliteOptions sqliteOptions)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = SqlitePragmaConnectionInterceptor.BuildDatabasePragmaSql(sqliteOptions);
        pragma.ExecuteNonQuery();

        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();
    }

    public static async Task EnsureAsync(SqliteConnection connection, ChoboSqliteOptions sqliteOptions, CancellationToken cancellationToken = default)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = SqlitePragmaConnectionInterceptor.BuildDatabasePragmaSql(sqliteOptions);
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        await using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        await schema.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string PathForDataDirectory(string dataDirectory) =>
        Path.Combine(dataDirectory, FileName);

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS ApplicationLogEntries (
            Id INTEGER NOT NULL CONSTRAINT PK_ApplicationLogEntries PRIMARY KEY AUTOINCREMENT,
            Timestamp INTEGER,
            Level VARCHAR(10),
            Exception TEXT,
            RenderedMessage TEXT,
            OperationId TEXT NULL,
            Properties TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Timestamp ON ApplicationLogEntries (Timestamp);
        CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Level_Timestamp ON ApplicationLogEntries (Level, Timestamp);
        CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_OperationId_Timestamp ON ApplicationLogEntries (OperationId, Timestamp);
        CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Timestamp_Id ON ApplicationLogEntries (Timestamp, Id);
        CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_OperationId_Timestamp_Id ON ApplicationLogEntries (OperationId, Timestamp, Id);
        """;
}
