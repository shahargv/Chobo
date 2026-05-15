namespace ChoboServer.Options;

public sealed class ChoboSqliteSelfBackupOptions
{
    public bool Enabled { get; set; }
    public string? Directory { get; set; }
    public TimeSpan BackupInterval { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}
