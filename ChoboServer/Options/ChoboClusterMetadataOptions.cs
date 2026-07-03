namespace ChoboServer.Options;

public sealed class ChoboClusterMetadataOptions
{
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(30);
}
