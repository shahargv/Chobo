namespace ChoboServer.Services;

public static class SqlLogRedactor
{
    public static string Preview(string sql, IReadOnlyList<string>? sensitiveValues = null)
    {
        var redacted = sql;
        foreach (var value in sensitiveValues ?? [])
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            redacted = redacted.Replace(ClickHouseSql.Literal(value), "'***REDACTED***'", StringComparison.Ordinal);
            redacted = redacted.Replace(value, "***REDACTED***", StringComparison.Ordinal);
        }

        var compact = string.Join(' ', redacted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 400 ? compact : compact[..400] + "...";
    }
}
