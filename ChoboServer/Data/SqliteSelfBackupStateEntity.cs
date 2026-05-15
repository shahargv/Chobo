namespace ChoboServer.Data;

public sealed class SqliteSelfBackupStateEntity
{
    public int Id { get; set; } = 1;
    public DateTimeOffset? LastBackupAt { get; set; }
    public string? LastBackupPath { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}
