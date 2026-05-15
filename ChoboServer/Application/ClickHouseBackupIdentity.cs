namespace ChoboServer.Application;

internal static class ClickHouseBackupIdentity
{
    private const char Separator = '\u001f';

    public static string Table(string database, string table) =>
        string.Concat(database, Separator, table);

    public static string Shard(string database, string table, int sourceShardNumber) =>
        string.Concat(Table(database, table), Separator, sourceShardNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
