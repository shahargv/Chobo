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
        bool allowDifferentTableDefinition,
        IReadOnlyDictionary<string, JsonElement> settings,
        string? password = null,
        bool useSamePasswordForBaseBackup = false)
    {
        var from = ClickHouseSql.Qualified(backupTable.Database, backupTable.Table);
        var to = ClickHouseSql.Qualified(restoreShard.RestoreDatabase, restoreShard.RestoreTableName);
        var choboSettings = new List<(string Name, string Value)>();
        if (allowNonEmptyTables)
        {
            choboSettings.Add(("allow_non_empty_tables", "1"));
        }
        if (allowDifferentTableDefinition)
        {
            choboSettings.Add(("allow_different_table_def", "1"));
        }
        if (password is not null)
        {
            choboSettings.Add(("password", ClickHouseSql.Literal(password)));
            if (useSamePasswordForBaseBackup)
            {
                choboSettings.Add(("use_same_password_for_base_backup", "1"));
            }
        }
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
