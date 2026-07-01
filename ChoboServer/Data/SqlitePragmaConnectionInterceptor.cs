using System.Data.Common;
using ChoboServer.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace ChoboServer.Data;

public sealed class SqlitePragmaConnectionInterceptor(IOptionsMonitor<ChoboSqliteOptions> options) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        var current = options.CurrentValue;
        using var command = sqlite.CreateCommand();
        command.CommandText = BuildConnectionPragmaSql(current);
        command.ExecuteNonQuery();
    }

    private async Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        var current = options.CurrentValue;
        await using var command = sqlite.CreateCommand();
        command.CommandText = BuildConnectionPragmaSql(current);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string BuildConnectionPragmaSql(ChoboSqliteOptions options) => $"""
        PRAGMA busy_timeout={BusyTimeoutMilliseconds(options)};
        PRAGMA synchronous={NormalizeSynchronous(options.Synchronous)};
        PRAGMA wal_autocheckpoint={NormalizeWalAutoCheckpoint(options.WalAutoCheckpoint)};
        """;

    public static string BuildDatabasePragmaSql(ChoboSqliteOptions options) => $"""
        PRAGMA journal_mode={NormalizeJournalMode(options.JournalMode)};
        PRAGMA synchronous={NormalizeSynchronous(options.Synchronous)};
        PRAGMA wal_autocheckpoint={NormalizeWalAutoCheckpoint(options.WalAutoCheckpoint)};
        """;

    public static void Validate(ChoboSqliteOptions options)
    {
        _ = NormalizeJournalMode(options.JournalMode);
        _ = NormalizeSynchronous(options.Synchronous);
        _ = BusyTimeoutMilliseconds(options);
        _ = NormalizeWalAutoCheckpoint(options.WalAutoCheckpoint);
    }

    private static string NormalizeJournalMode(string? value)
    {
        var normalized = NormalizeKeyword(value, "WAL");
        return normalized is "DELETE" or "TRUNCATE" or "PERSIST" or "MEMORY" or "WAL" or "OFF"
            ? normalized
            : throw new InvalidOperationException("SQLite journal mode must be one of DELETE, TRUNCATE, PERSIST, MEMORY, WAL, or OFF.");
    }

    private static string NormalizeSynchronous(string? value)
    {
        var normalized = NormalizeKeyword(value, "NORMAL");
        return normalized is "OFF" or "NORMAL" or "FULL" or "EXTRA"
            ? normalized
            : throw new InvalidOperationException("SQLite synchronous mode must be one of OFF, NORMAL, FULL, or EXTRA.");
    }

    private static string NormalizeKeyword(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToUpperInvariant();
        return normalized.All(static c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? normalized
            : throw new InvalidOperationException("SQLite PRAGMA values must be simple SQLite keywords.");
    }

    private static int BusyTimeoutMilliseconds(ChoboSqliteOptions options)
    {
        if (options.BusyTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException("SQLite busy timeout must be zero or greater.");
        }

        return options.BusyTimeout.TotalMilliseconds > int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(options.BusyTimeout.TotalMilliseconds);
    }

    private static int NormalizeWalAutoCheckpoint(int value) =>
        value >= 0 ? value : throw new InvalidOperationException("SQLite WAL auto-checkpoint must be zero or greater.");
}
