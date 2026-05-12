using System.Text.Json;

namespace Chobo.Contracts;

public sealed record AuditEntryDto(long Id, DateTimeOffset Timestamp, Guid? ActorUserId, string ActorName, string Action, string EntityType, string? EntityId, JsonElement Details);

public sealed record ClearAuditEntriesRequest(DateTimeOffset Before);
