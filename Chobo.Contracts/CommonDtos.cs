namespace Chobo.Contracts;

public sealed record ErrorResponse(string Error);

public sealed record ServerVersionDto(string ProductName, string ProductVersion, int ApiVersion, int SchemaVersion, int DatabaseSchemaVersion);

public sealed record InstallStatusDto(bool RequiresInstallation, string Message);

public sealed record InstallRequest(string? AdminUser);

public sealed record InstallResponse(Guid UserId, string UserName, string AccessToken);

public sealed record QueryWindow(DateTimeOffset? StartTime, DateTimeOffset? EndTime, int? Last);

public sealed record PagedResultDto<T>(IReadOnlyList<T> Items, int Offset, int Limit, int TotalCount);
