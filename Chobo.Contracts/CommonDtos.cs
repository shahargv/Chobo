namespace Chobo.Contracts;

public sealed record ErrorResponse(string Error);

public sealed record ServerVersionDto(string ProductName, string ProductVersion, int ApiVersion, int SchemaVersion, int DatabaseSchemaVersion);

public sealed record QueryWindow(DateTimeOffset? StartTime, DateTimeOffset? EndTime, int? Last);

public sealed record PagedResultDto<T>(IReadOnlyList<T> Items, int Offset, int Limit, int TotalCount);

