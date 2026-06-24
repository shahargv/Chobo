namespace ChoboServer.Options;

public sealed class ChoboBackupRestoreOptions
{
    public int MaxDop { get; set; } = 3;
    public int QueueCapacity { get; set; } = 100;
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan SchedulerMissedRunGracePeriod { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int ManifestCheckpointShardInterval { get; set; } = 20;
    public TimeSpan ManifestWriteTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class RetentionManagementOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
    public int MaxDop { get; set; } = 2;
}

public sealed class BackupsGarbageCollectorOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
    public int MaxDop { get; set; } = 2;
}

public sealed class BackupStorageOperationOptions
{
    public TimeSpan S3RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public int S3MaxErrorRetry { get; set; } = 5;
    public int S3DeleteBatchSize { get; set; } = 1000;
}
