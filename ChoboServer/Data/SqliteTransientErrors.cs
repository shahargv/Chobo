using Microsoft.Data.Sqlite;

namespace ChoboServer.Data;

public static class SqliteTransientErrors
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteBusySnapshot = 517;

    public static bool IsTransientLock(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite &&
                sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked &&
                sqlite.SqliteExtendedErrorCode != SqliteBusySnapshot)
            {
                return true;
            }
        }

        return false;
    }

    public static TimeSpan RetryDelay(int attempt)
    {
        var capped = Math.Clamp(attempt, 0, 4);
        var baseMilliseconds = 100 * (1 << capped);
        return TimeSpan.FromMilliseconds(baseMilliseconds + Random.Shared.Next(0, baseMilliseconds / 2));
    }
}
