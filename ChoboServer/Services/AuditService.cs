using ChoboServer.Data;

namespace ChoboServer.Services;

public sealed class AuditService(ChoboDbContext db, ActorContext actor)
{
    public async Task RecordAsync(string action, string entityType, string? entityId, object? details = null)
    {
        db.AuditEntries.Add(new AuditEntryEntity
        {
            ActorUserId = actor.UserId,
            ActorName = actor.ActorName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = AuditDetails.Serialize(details)
        });
        await db.SaveChangesAsync();
    }
}
