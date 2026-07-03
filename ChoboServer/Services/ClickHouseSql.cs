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

    public static string RewriteCreateTableNameIfNotExists(string createSql, string database, string table)
    {
        var rewritten = RewriteCreateTableName(createSql, database, table);
        return rewritten.StartsWith("CREATE TABLE IF NOT EXISTS ", StringComparison.OrdinalIgnoreCase)
            ? rewritten
            : rewritten.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase)
                ? "CREATE TABLE IF NOT EXISTS " + rewritten["CREATE TABLE ".Length..]
                : rewritten;
    }

    public static string RewriteReplicatedMergeTreeToLocalMergeTree(string createSql)
    {
        return TryRewriteReplicatedMergeTreeToLocalMergeTree(createSql, out var rewritten)
            ? rewritten
            : createSql;
    }

    public static string RewriteReplicatedMergeTreeToLocalMergeTreeOrThrow(string createSql)
    {
        if (TryRewriteReplicatedMergeTreeToLocalMergeTree(createSql, out var rewritten))
        {
            return rewritten;
        }

        throw new InvalidOperationException("Could not rewrite replicated MergeTree table DDL to a local MergeTree engine for single-node restore.");
    }

    public static bool IsReplicatedMergeTreeEngine(string engine) =>
        engine.Contains("Replicated", StringComparison.OrdinalIgnoreCase) && engine.Contains("MergeTree", StringComparison.OrdinalIgnoreCase);

    private static bool TryRewriteReplicatedMergeTreeToLocalMergeTree(string createSql, out string rewritten)
    {
        var engineIndex = createSql.IndexOf("ENGINE", StringComparison.OrdinalIgnoreCase);
        if (engineIndex < 0)
        {
            rewritten = createSql;
            return false;
        }

        var replicatedIndex = createSql.IndexOf("Replicated", engineIndex, StringComparison.OrdinalIgnoreCase);
        if (replicatedIndex < 0)
        {
            rewritten = createSql;
            return false;
        }

        var open = createSql.IndexOf('(', replicatedIndex);
        if (open < 0)
        {
            rewritten = createSql;
            return false;
        }

        var replicatedEngine = createSql[replicatedIndex..open].Trim();
        if (!replicatedEngine.StartsWith("Replicated", StringComparison.OrdinalIgnoreCase) || !replicatedEngine.EndsWith("MergeTree", StringComparison.OrdinalIgnoreCase))
        {
            rewritten = createSql;
            return false;
        }

        var close = FindMatchingCloseParenthesis(createSql, open);
        if (close < 0)
        {
            rewritten = createSql;
            return false;
        }

        var args = SplitTopLevelArguments(createSql[(open + 1)..close]);
        if (args.Count < 2)
        {
            rewritten = createSql;
            return false;
        }

        var localEngine = replicatedEngine["Replicated".Length..];
        var remainingArgs = args.Skip(2).ToList();
        var replacement = remainingArgs.Count == 0
            ? localEngine
            : $"{localEngine}({string.Join(", ", remainingArgs)})";
        rewritten = createSql[..replicatedIndex] + replacement + createSql[(close + 1)..];
        return true;
    }
    public static string NormalizeCreateTableName(string createSql) =>
        RewriteCreateTableName(createSql, "__chobo_schema__.__table__");

    private static int FindMatchingCloseParenthesis(string value, int openIndex)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = openIndex; i < value.Length; i++)
        {
            var ch = value[i];
            if (quote != '\0')
            {
                if (ch == quote && (i == 0 || value[i - 1] != '\\'))
                {
                    quote = '\0';
                }
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch == '(')
            {
                depth++;
                continue;
            }
            if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevelArguments(string value)
    {
        var args = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (quote != '\0')
            {
                if (ch == quote && (i == 0 || value[i - 1] != '\\'))
                {
                    quote = '\0';
                }
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch == '(')
            {
                depth++;
                continue;
            }
            if (ch == ')')
            {
                depth--;
                continue;
            }
            if (ch == ',' && depth == 0)
            {
                args.Add(value[start..i].Trim());
                start = i + 1;
            }
        }

        args.Add(value[start..].Trim());
        return args;
    }

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
