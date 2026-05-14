using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class BackupApplicationService(
    ChoboDbContext db,
    BackupRestoreQueues queues,
    AuditService audit,
    ActorContext actor,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupApplicationService>();

    public async Task<BackupDto> ManualAsync(ManualBackupRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ClusterId == Guid.Empty)
        {
            throw new ArgumentException("Cluster id is required.");
        }
        if (request.TargetId == Guid.Empty)
        {
            throw new ArgumentException("Target id is required.");
        }
        if (request.Selector.Version != 1)
        {
            throw new ArgumentException("Only selector version 1 is supported.");
        }
        if (!await db.ClickHouseClusters.AnyAsync(x => x.Id == request.ClusterId && !x.IsDeleted, cancellationToken))
        {
            throw new ArgumentException("Cluster was not found.");
        }
        if (!await db.BackupTargets.AnyAsync(x => x.Id == request.TargetId && !x.IsDeleted, cancellationToken))
        {
            throw new ArgumentException("Target was not found.");
        }

        var backup = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            SourceClusterId = request.ClusterId,
            TargetId = request.TargetId,
            ManualRequestJson = JsonSerializer.Serialize(request, JsonOptions),
            RequestedByUserId = actor.UserId,
            RequestedByName = actor.ActorName
        };

        db.Backups.Add(backup);
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Manual backup {BackupId} created by {ActorName} for cluster {ClusterId} target {TargetId}.", backup.Id, actor.ActorName, request.ClusterId, request.TargetId);
        await audit.RecordAsync("created", AuditEntityType.Backup, backup.Id.ToString(), new { backup.TriggerType, backup.SourceClusterId, backup.TargetId });
        await queues.QueueBackupAsync(backup.Id, cancellationToken);
        _logger.Information("Manual backup {BackupId} queued.", backup.Id);
        await audit.RecordAsync("queued", AuditEntityType.Backup, backup.Id.ToString(), new { reason = "manual" });

        return BackupRestoreMapping.ToDto(await LoadAsync(backup.Id, cancellationToken) ?? backup);
    }

    public async Task<IReadOnlyList<BackupDto>> ListAsync(Guid? policyId, string? clusterName, string? tableName, BackupRunStatus? status, CancellationToken cancellationToken = default)
    {
        var query = db.Backups
            .Include(x => x.SourceCluster)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .AsQueryable();

        if (policyId is not null)
        {
            query = query.Where(x => x.PolicyId == policyId);
        }
        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(clusterName))
        {
            query = query.Where(x => x.SourceCluster != null && x.SourceCluster.Name == clusterName);
        }
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            query = query.Where(x => x.Tables.Any(t => t.Table == tableName || t.Database + "." + t.Table == tableName));
        }

        return (await query.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(cancellationToken))
            .Select(BackupRestoreMapping.ToDto)
            .ToList();
    }

    public async Task<BackupDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await LoadAsync(id, cancellationToken) is { } backup ? BackupRestoreMapping.ToDto(backup) : null;

    public async Task<BackupDto?> PinAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var backup = await LoadAsync(id, cancellationToken);
        if (backup is null)
        {
            return null;
        }

        backup.IsPinned = true;
        backup.PinnedAt = DateTimeOffset.UtcNow;
        backup.PinnedByUserId = actor.UserId;
        backup.PinnedByName = actor.ActorName;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("pin", AuditEntityType.Backup, id.ToString(), new { backup.PinnedByUserId, backup.PinnedByName });
        return BackupRestoreMapping.ToDto(backup);
    }

    public async Task<BackupDto?> UnpinAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var backup = await LoadAsync(id, cancellationToken);
        if (backup is null)
        {
            return null;
        }

        var previous = new { backup.IsPinned, backup.PinnedAt, backup.PinnedByUserId, backup.PinnedByName };
        backup.IsPinned = false;
        backup.PinnedAt = null;
        backup.PinnedByUserId = null;
        backup.PinnedByName = null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("unpin", AuditEntityType.Backup, id.ToString(), previous);
        return BackupRestoreMapping.ToDto(backup);
    }

    public async Task<BackupDto?> RequestDeleteAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        var backup = await LoadAsync(id, cancellationToken);
        if (backup is null)
        {
            return null;
        }
        if (backup.Status is BackupRunStatus.Queued or BackupRunStatus.Running)
        {
            throw new ArgumentException("Queued or running backups cannot be deleted.");
        }
        if (backup.IsPinned && !force)
        {
            throw new ArgumentException("Pinned backups require force delete.");
        }
        if (IsDeletedOrDeleteRequested(backup.Status))
        {
            return BackupRestoreMapping.ToDto(backup);
        }

        backup.Status = BackupRunStatus.ManualDeleteRequested;
        backup.DeletionReason = force && backup.IsPinned ? "manual-force" : "manual";
        backup.DeletionRequestedAt ??= DateTimeOffset.UtcNow;
        backup.DeletionError = null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("delete-requested", AuditEntityType.Backup, id.ToString(), new { reason = backup.DeletionReason, force, backup.IsPinned });
        return BackupRestoreMapping.ToDto(backup);
    }

    private Task<BackupEntity?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Backups
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    private static bool IsDeletedOrDeleteRequested(BackupRunStatus status) =>
        status is BackupRunStatus.ManualDeleteRequested or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeleteRequested or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleteStarted or
            BackupRunStatus.BackupExpiredDeleted;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
