namespace ChoboServer.Options;

public sealed class ChoboDataRetentionOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
    public DateTimeOffset? LogsBefore { get; set; }
    public DateTimeOffset? AuditsBefore { get; set; }
}
