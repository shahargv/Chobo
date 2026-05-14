namespace Chobo.Contracts;

public sealed record ErrorResponse(string Error);

public sealed record ServerVersionDto(string ProductName, string ServerVersion, int ApiVersion, int SchemaVersion, int DatabaseSchemaVersion);

public sealed record QueryWindow(DateTimeOffset? StartTime, DateTimeOffset? EndTime, int? Last);
