namespace Chobo.Contracts;

public sealed record ApplicationLogEntryDto(long Id, DateTimeOffset Timestamp, string Level, string Category, string Message, string? Exception);

public sealed record ClearApplicationLogsRequest(DateTimeOffset Before);
