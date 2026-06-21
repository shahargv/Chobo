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
    IBackupRestoreQueues queues,
    IClickHouseAdapter clickHouse,
    IAuditService audit,
    ActorContext actor,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupApplicationService>();

    public async Task<BackupDto> ManualAsync(ManualBackupRequest request, CancellationToken cancellationToken = default)
    {
        BackupPolicyEntity? policy = null;
        var clusterId = request.ClusterId;
        var targetId = request.TargetId;
        var selector = request.Selector;
        var contentMode = request.SchemaOnly ? BackupContentMode.SchemaOnly : BackupContentMode.SchemaAndData;
        if (request.PolicyId is { } policyId)
        {
            policy = await db.BackupPolicies.FirstOrDefaultAsync(x => x.Id == policyId && !x.IsDeleted, cancellationToken);
            if (policy is null)
            {
                throw new ArgumentException("Policy was not found.");
            }

            clusterId = policy.SourceClusterId;
            targetId = policy.TargetId;
            selector = JsonSerializer.Deserialize<PolicySelector>(policy.SelectorJson, JsonOptions) ?? PolicySelector.Empty;
            contentMode = policy.ContentMode;
        }
        else if (request.BackupType == BackupType.Incremental)
        {
            throw new ArgumentException("Manual incremental backups require PolicyId.");
        }
        if (contentMode == BackupContentMode.SchemaOnly && request.BackupType == BackupType.Incremental)
        {
            throw new ArgumentException("Schema-only backups must be full backups.");
        }

        if (clusterId == Guid.Empty)
        {
            throw new ArgumentException("Cluster id is required.");
        }
        if (contentMode == BackupContentMode.SchemaAndData && (targetId is null || targetId == Guid.Empty))
        {
            throw new ArgumentException("Target id is required.");
        }
        if (selector.Version != 1)
        {
            throw new ArgumentException("Only selector version 1 is supported.");
        }
        if (!await db.ClickHouseClusters.AnyAsync(x => x.Id == clusterId && !x.IsDeleted, cancellationToken))
        {
            throw new ArgumentException("Cluster was not found.");
        }
        if (targetId is { } concreteTargetId && concreteTargetId != Guid.Empty && !await db.BackupTargets.AnyAsync(x => x.Id == concreteTargetId && !x.IsDeleted, cancellationToken))
        {
            throw new ArgumentException("Target was not found.");
        }

        var storedRequest = request.PolicyId is null
            ? request
            : request with { ClusterId = clusterId, TargetId = targetId, Selector = selector, SchemaOnly = contentMode == BackupContentMode.SchemaOnly };
        var backup = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            BackupType = contentMode == BackupContentMode.SchemaOnly ? BackupType.Full : request.BackupType,
            ContentMode = contentMode,
            SourceClusterId = clusterId,
            TargetId = targetId,
            PolicyId = request.PolicyId,
            ManualRequestJson = JsonSerializer.Serialize(storedRequest, JsonOptions),
            RequestedByUserId = actor.UserId,
            RequestedByName = actor.ActorName
        };

        db.Backups.Add(backup);
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Manual backup {BackupId} created by {ActorName} for cluster {ClusterId} target {TargetId} type {BackupType} contentMode={ContentMode}.", backup.Id, actor.ActorName, clusterId, targetId, backup.BackupType, backup.ContentMode);
        await audit.RecordAsync("created", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, backup.TriggerType, backup.BackupType, backup.ContentMode, backup.SourceClusterId, backup.TargetId, backup.PolicyId });
        await queues.QueueBackupAsync(backup.Id, cancellationToken);
        _logger.Information("Manual backup {BackupId} queued.", backup.Id);
        await audit.RecordAsync("queued", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, reason = "manual" });

        return BackupRestoreMapping.ToDto(await LoadAsync(backup.Id, cancellationToken) ?? backup);
    }
    public async Task<IReadOnlyList<BackupDto>> ListAsync(Guid? policyId, string? clusterName, string? tableName, BackupRunStatus? status, bool includeTables = true, CancellationToken cancellationToken = default)
    {
        var query = includeTables
            ? db.Backups.Include(x => x.SourceCluster).Include(x => x.Tables).ThenInclude(x => x.Shards).AsQueryable()
            : db.Backups.Include(x => x.SourceCluster).AsQueryable();

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

        if (includeTables)
        {
            return (await query.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(cancellationToken))
                .Select(backup => BackupRestoreMapping.ToDto(backup))
                .ToList();
        }

        var summaries = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new { Backup = x, TableCount = x.Tables.Count })
            .ToListAsync(cancellationToken);
        return summaries
            .Select(x => BackupRestoreMapping.ToDto(x.Backup, x.TableCount, includeTables: false))
            .ToList();
    }

    public async Task<BackupDto?> GetAsync(Guid id, bool includeTables = true, CancellationToken cancellationToken = default)
    {
        if (includeTables)
        {
            return await LoadAsync(id, cancellationToken) is { } backup ? BackupRestoreMapping.ToDto(backup) : null;
        }

        var summary = await db.Backups
            .Where(x => x.Id == id)
            .Select(x => new { Backup = x, TableCount = x.Tables.Count })
            .FirstOrDefaultAsync(cancellationToken);
        return summary is null ? null : BackupRestoreMapping.ToDto(summary.Backup, summary.TableCount, includeTables: false);
    }

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
        await audit.RecordAsync("pin", AuditEntityType.Backup, id.ToString(), new { operationId = id, backup.PinnedByUserId, backup.PinnedByName });
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

    public async Task<BackupDto?> RequestDeleteAsync(Guid id, bool force = false, bool confirmDestructive = false, CancellationToken cancellationToken = default)
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
        if (!confirmDestructive)
        {
            throw new ArgumentException("Deleting a backup is destructive and requires ConfirmDestructive=true.");
        }
        if (backup.IsPinned && !force)
        {
            throw new ArgumentException("Pinned backups require force delete.");
        }
        var dependentBackupIds = await DependentBackupIdsAsync(id, cancellationToken);
        if (dependentBackupIds.Count > 0 && !force && await HasPinnedBackupsAsync(dependentBackupIds, cancellationToken))
        {
            throw new ArgumentException("Pinned incremental backups depending on this full backup require force delete.");
        }
        if (IsDeletedOrDeleteRequested(backup.Status))
        {
            return BackupRestoreMapping.ToDto(backup);
        }

        List<BackupEntity> dependents = [];
        if (dependentBackupIds.Count > 0)
        {
            var deletedStatuses = DeletedStatuses;
            dependents = await db.Backups
                .Where(x => dependentBackupIds.Contains(x.Id) && !deletedStatuses.Contains(x.Status))
                .ToListAsync(cancellationToken);
            foreach (var dependent in dependents)
            {
                dependent.Status = BackupRunStatus.ManualDeleteRequested;
                dependent.DeletionReason = force ? "manual-parent-force" : "manual-parent";
                dependent.DeletionRequestedAt ??= DateTimeOffset.UtcNow;
                dependent.DeletionError = null;
            }
        }

        backup.Status = BackupRunStatus.ManualDeleteRequested;
        backup.DeletionReason = force && backup.IsPinned ? "manual-force" : "manual";
        backup.DeletionRequestedAt ??= DateTimeOffset.UtcNow;
        backup.DeletionError = null;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var dependent in dependents)
        {
            await audit.RecordAsync("dependent-delete-requested", AuditEntityType.Backup, dependent.Id.ToString(), new { parentBackupId = id, force });
        }
        await audit.RecordAsync("delete-requested", AuditEntityType.Backup, id.ToString(), new { operationId = id, reason = backup.DeletionReason, force, backup.IsPinned });
        return BackupRestoreMapping.ToDto(backup);
    }

    public async Task<BackupDto?> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (backup is null)
        {
            return null;
        }
        if (backup.Status is not (BackupRunStatus.Queued or BackupRunStatus.Running))
        {
            throw new ArgumentException("Only queued or running backups can be canceled.");
        }

        using var operationCorrelationScope = OperationCorrelationContext.Push(backup.Id.ToString());
        using var operationLogScope = Serilog.Context.LogContext.PushProperty("OperationId", backup.Id.ToString());

        var now = DateTimeOffset.UtcNow;
        backup.Status = BackupRunStatus.Canceled;
        backup.CompletedAt = now;
        backup.FailureReason = $"Backup canceled by {actor.ActorName}.";
        backup.Error = backup.FailureReason;
        backup.DeletionReason = "canceled";
        backup.DeletionRequestedAt ??= now;
        backup.DeletionError = null;
        foreach (var table in backup.Tables.Where(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running))
        {
            table.Status = BackupTableStatus.Skipped;
            table.Error = "Backup canceled.";
            table.CompletedAt ??= now;
            foreach (var shard in table.Shards.Where(x => x.Status is BackupTableStatus.Queued or BackupTableStatus.Running))
            {
                shard.Status = BackupTableStatus.Skipped;
                shard.Error = "Backup canceled.";
                shard.CompletedAt ??= now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var killResults = await KillBackupOperationsAsync(backup, cancellationToken);
        await audit.RecordAsync("canceled", AuditEntityType.Backup, id.ToString(), new { operationId = id, actor.UserId, actor.ActorName, killed = killResults.Killed, killFailures = killResults.Failures });
        return BackupRestoreMapping.ToDto(backup);
    }

    private async Task<(int Killed, IReadOnlyList<string> Failures)> KillBackupOperationsAsync(BackupEntity backup, CancellationToken cancellationToken)
    {
        if (backup.SourceCluster is null)
        {
            return (0, []);
        }

        var killed = 0;
        var failures = new List<string>();
        var operations = backup.Tables
            .SelectMany(table => table.Shards.Count == 0
                ? string.IsNullOrWhiteSpace(table.ClickHouseOperationId) ? [] : [new { Endpoint = (ClickHouseNodeEndpoint?)null, OperationId = table.ClickHouseOperationId! }]
                : table.Shards
                    .Where(shard => !string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
                    .Select(shard => new { Endpoint = (ClickHouseNodeEndpoint?)new ClickHouseNodeEndpoint(shard.Host, shard.Port, shard.UseTls), OperationId = shard.ClickHouseOperationId! }))
            .DistinctBy(x => x.OperationId)
            .ToList();

        foreach (var operation in operations)
        {
            try
            {
                if (operation.Endpoint is { } endpoint)
                {
                    await clickHouse.KillQueryAsync(endpoint, backup.SourceCluster, operation.OperationId, cancellationToken);
                }
                else
                {
                    await clickHouse.KillQueryAsync(backup.SourceCluster, operation.OperationId, cancellationToken);
                }

                killed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{operation.OperationId}: {ex.Message}");
                _logger.Warning(ex, "Failed to kill ClickHouse backup operation {OperationId} for backup {BackupId}.", operation.OperationId, backup.Id);
            }
        }

        return (killed, failures);
    }
    private Task<BackupEntity?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Backups
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    private async Task<IReadOnlyList<Guid>> DependentBackupIdsAsync(Guid fullBackupId, CancellationToken cancellationToken)
    {
        var tableDependents = await db.BackupTables
            .Where(x => x.ParentFullBackupTable != null && x.ParentFullBackupTable.BackupId == fullBackupId)
            .Select(x => x.BackupId)
            .ToListAsync(cancellationToken);
        var shardDependents = await db.BackupTableShards
            .Where(x => x.ParentFullBackupTableShard != null && x.ParentFullBackupTableShard.BackupTable!.BackupId == fullBackupId)
            .Select(x => x.BackupTable!.BackupId)
            .ToListAsync(cancellationToken);
        return tableDependents.Concat(shardDependents).Distinct().ToList();
    }

    private async Task<bool> HasPinnedBackupsAsync(IReadOnlyList<Guid> backupIds, CancellationToken cancellationToken) =>
        await db.Backups.AnyAsync(x => backupIds.Contains(x.Id) && x.IsPinned, cancellationToken);

    private static bool IsDeletedOrDeleteRequested(BackupRunStatus status) =>
        status is BackupRunStatus.ManualDeleteRequested or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeleteRequested or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleteStarted or
            BackupRunStatus.BackupExpiredDeleted;

    private static readonly BackupRunStatus[] DeletedStatuses =
    [
        BackupRunStatus.ManualDeleteRequested,
        BackupRunStatus.ManualDeleted,
        BackupRunStatus.FailedBackupDeleteRequested,
        BackupRunStatus.FailedBackupDeletedByGarbageCollector,
        BackupRunStatus.BackupExpiredDeleteStarted,
        BackupRunStatus.BackupExpiredDeleted
    ];

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}


