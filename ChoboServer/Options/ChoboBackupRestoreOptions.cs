namespace ChoboServer.Options;

public sealed class ChoboBackupRestoreOptions
{
    public int MaxDop { get; set; } = 3;
    public int QueueCapacity { get; set; } = 100;
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan SchedulerMissedRunGracePeriod { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
