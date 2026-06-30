using System.Text.Json;
using ChoboServer.Data;

namespace ChoboServer.Services;

public static class ClickHouseRestoreSqlBuilder
{
    public static string Build(
        BackupTableEntity backupTable,
        RestoreTableShardEntity restoreShard,
        ClickHouseStorageDestination destination,
        bool allowNonEmptyTables,
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        var from = ClickHouseSql.Qualified(backupTable.Database, backupTable.Table);
        var to = ClickHouseSql.Qualified(restoreShard.RestoreDatabase, restoreShard.RestoreTableName);
        var choboSettings = allowNonEmptyTables ? new[] { ("allow_non_empty_tables", "1") } : [];
        var settingsClause = ClickHouseAdvancedSettings.ToSettingsClause(settings, choboSettings.Concat(destination.Settings).ToArray());
        return $"RESTORE TABLE {from} AS {to} FROM {destination.Expression}{settingsClause} ASYNC";
    }

    public static string RedactForPreview(string sql, IReadOnlyList<string>? sensitiveValues = null)
    {
        var redacted = sql;
        foreach (var value in sensitiveValues ?? [])
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            redacted = redacted.Replace(ClickHouseSql.Literal(value), "'REDACTED'", StringComparison.Ordinal);
            redacted = redacted.Replace(value, "REDACTED", StringComparison.Ordinal);
        }

        return redacted;
    }
}
