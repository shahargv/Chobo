namespace ChoboServer.Options;

public sealed class ChoboDatabaseLoggingOptions
{
    public TimeSpan SlowQueryThreshold { get; set; } = TimeSpan.FromSeconds(2);
}
