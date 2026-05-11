using System.Text;

namespace ChoboServer.Services;

public static class ClickHouseSql
{
    public static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier is required.");
        }

        return "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    public static string Qualified(string database, string table) =>
        $"{Identifier(database)}.{Identifier(table)}";

    public static string Literal(string value) =>
        "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    public static string S3(string endpoint, string accessKey, string secretKey) =>
        $"S3({Literal(endpoint)}, {Literal(accessKey)}, {Literal(secretKey)})";

    public static string RewriteCreateTableName(string createSql, string database, string table)
    {
        return RewriteCreateTableName(createSql, Qualified(database, table));
    }

    public static string NormalizeCreateTableName(string createSql) =>
        RewriteCreateTableName(createSql, "__chobo_schema__.__table__");

    private static string RewriteCreateTableName(string createSql, string replacement)
    {
        var marker = "CREATE TABLE ";
        var index = createSql.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return createSql;
        }

        var start = index + marker.Length;
        var builder = new StringBuilder();
        builder.Append(createSql.AsSpan(0, start));
        builder.Append(replacement);

        var restStart = createSql.IndexOf('(', start);
        if (restStart < 0)
        {
            return createSql;
        }

        builder.Append(' ');
        builder.Append(createSql.AsSpan(restStart));
        return builder.ToString();
    }
}
