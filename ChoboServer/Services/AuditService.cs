using ChoboServer.Data;

namespace ChoboServer.Services;

public sealed class AuditService(ChoboDbContext db, ActorContext actor)
{
    public async Task RecordAsync(string action, AuditEntityType entityType, string? entityId, object? details = null)
    {
        db.AuditEntries.Add(new AuditEntryEntity
        {
            ActorUserId = actor.UserId,
            ActorName = actor.ActorName,
            Action = action,
            EntityType = AuditEntityTypes.ToStorageValue(entityType),
            EntityId = entityId,
            Details = AuditDetails.Serialize(details)
        });
        await db.SaveChangesAsync();
    }
}

public enum AuditEntityType
{
    AccessToken,
    ApplicationLog,
    Audit,
    Backup,
    BackupPolicy,
    BackupSchedule,
    BackupTarget,
    BackupTable,
    BackupTableShard,
    Cluster,
    Config,
    Data,
    DataRetention,
    Restore,
    RestoreTable,
    RestoreTableShard,
    Server,
    User
}

public static class AuditEntityTypes
{
    public static string ToStorageValue(AuditEntityType entityType) =>
        entityType switch
        {
            AuditEntityType.AccessToken => "access-token",
            AuditEntityType.ApplicationLog => "application-log",
            AuditEntityType.Audit => "audit",
            AuditEntityType.Backup => "backup",
            AuditEntityType.BackupPolicy => "backup-policy",
            AuditEntityType.BackupSchedule => "backup-schedule",
            AuditEntityType.BackupTarget => "backup-target",
            AuditEntityType.BackupTable => "backup-table",
            AuditEntityType.BackupTableShard => "backup-table-shard",
            AuditEntityType.Cluster => "cluster",
            AuditEntityType.Config => "config",
            AuditEntityType.Data => "data",
            AuditEntityType.DataRetention => "data-retention",
            AuditEntityType.Restore => "restore",
            AuditEntityType.RestoreTable => "restore-table",
            AuditEntityType.RestoreTableShard => "restore-table-shard",
            AuditEntityType.Server => "server",
            AuditEntityType.User => "user",
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unsupported audit entity type.")
        };
}
