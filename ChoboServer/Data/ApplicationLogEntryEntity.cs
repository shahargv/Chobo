namespace ChoboServer.Data;

public sealed class ApplicationLogEntryEntity
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "";
    public string? Exception { get; set; }
    public string RenderedMessage { get; set; } = "";
    public string? OperationId { get; set; }
    public string? Properties { get; set; }
}

