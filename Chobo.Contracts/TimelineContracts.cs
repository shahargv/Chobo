using System.Text.Json;

namespace Chobo.Contracts;

public sealed record LogEntryDto(long Id, DateTimeOffset Timestamp, string Level, string Category, string Message, string? Exception);

public sealed record AuditEntryDto(long Id, DateTimeOffset Timestamp, Guid? ActorUserId, string ActorName, string Action, string EntityType, string? EntityId, JsonElement Details);
