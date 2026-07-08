namespace ChoboServer.Options;

public sealed class ChoboSqliteOptions
{
    public string JournalMode { get; set; } = "WAL";
    public string Synchronous { get; set; } = "NORMAL";
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int WalAutoCheckpoint { get; set; } = 1000;
}
