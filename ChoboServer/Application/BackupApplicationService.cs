using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.Application;

public sealed class BackupApplicationService(
    ChoboDbContext db,
    IBackupRestoreQueues queues,
    BackupRestoreQueueApplicationService queueItems,
    BackupPreparationService preparation,
    IClickHouseAdapter clickHouse,
    IOptionsMonitor<ChoboBackupRestoreOptions> options,
    BackupRestoreOperationGate operationGate,
    IAuditService audit,
    ActorContext actor,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupApplicationService>();

    public async Task<ClickHouseSettingsPreviewDto> PreviewSettingsAsync(BackupSettingsPreviewRequest request, CancellationToken cancellationToken = default)
    {
        Guid? clusterId = request.ClusterId;
        BackupPolicyEntity? policy = null;
        if (request.PolicyId is { } policyId)
        {
            policy = await db.BackupPolicies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == policyId && !x.IsDeleted, cancellationToken)
                ?? throw new ArgumentException("Policy was not found.");
            clusterId = policy.SourceClusterId;
        }

        if (clusterId is null || clusterId == Guid.Empty)
        {
            throw new ArgumentException("Cluster id is required.");
        }

        var cluster = await db.ClickHouseClusters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clusterId && !x.IsDeleted, cancellationToken)
            ?? throw new ArgumentException("Cluster was not found.");

        return ClickHouseAdvancedSettings.MergeWithSources(
            ("cluster", ClickHouseAdvancedSettings.Deserialize(cluster.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup)),
            ("policy", policy is null ? ClickHouseAdvancedSettings.Empty : ClickHouseAdvancedSettings.Deserialize(policy.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup)));
    }
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

        var inheritedSettings = await PreviewSettingsAsync(new BackupSettingsPreviewRequest(clusterId, request.PolicyId), cancellationToken);
        var effectiveSettings = request.ClickHouseBackupSettings is null
            ? inheritedSettings.Settings
            : ClickHouseAdvancedSettings.Normalize(request.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup);

        var storedRequest = request.PolicyId is null
            ? request
            : request with { ClusterId = clusterId, TargetId = targetId, Selector = selector, SchemaOnly = contentMode == BackupContentMode.SchemaOnly, ClickHouseBackupSettings = effectiveSettings };
        var backup = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            BackupType = contentMode == BackupContentMode.SchemaOnly ? BackupType.Full : request.BackupType,
            ContentMode = contentMode,
            SourceClusterId = clusterId,
            TargetId = targetId,
            PolicyId = request.PolicyId,
            ManualRequestJson = JsonSerializer.Serialize(storedRequest with { ClickHouseBackupSettings = effectiveSettings }, JsonOptions),
            ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.SerializeNormalized(effectiveSettings),
            RequestedByUserId = actor.UserId,
            RequestedByName = actor.ActorName
        };

        await using var operationCreationGate = await operationGate.EnterAsync(cancellationToken);
        db.Backups.Add(backup);
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Manual backup {BackupId} created by {ActorName} for cluster {ClusterId} target {TargetId} type {BackupType} contentMode={ContentMode}.", backup.Id, actor.ActorName, clusterId, targetId, backup.BackupType, backup.ContentMode);
        await audit.RecordAsync("created", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, backup.TriggerType, backup.BackupType, backup.ContentMode, backup.SourceClusterId, backup.TargetId, backup.PolicyId });

        // After the backup row is committed, finish queue preparation even if the HTTP client disconnects.
        // Otherwise a slow inventory read can leave a permanent Queued backup with no queue item.
        var postCommitCancellationToken = CancellationToken.None;
        try
        {
            await preparation.PrepareQueueItemsAsync(backup.Id, postCommitCancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Manual backup {BackupId} queue item preparation failed before executor pickup; executor will retry preparation.", backup.Id);
            await audit.RecordAsync("queue-preparation-deferred", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, error = ex.Message });
        }
        await queues.QueueBackupAsync(backup.Id, backup.ContentMode, postCommitCancellationToken);
        _logger.Information("Manual backup {BackupId} queued.", backup.Id);
        await audit.RecordAsync("queued", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, reason = "manual" });

        return BackupRestoreMapping.ToDto(await LoadAsync(backup.Id, postCommitCancellationToken) ?? backup);
    }
    public async Task<IReadOnlyList<BackupDto>> ListAsync(Guid? policyId, string? clusterName, string? tableName, BackupRunStatus? status, DateTimeOffset? from, DateTimeOffset? to, bool includeTables = true, CancellationToken cancellationToken = default)
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
        if (from is not null)
        {
            query = query.Where(x => x.CreatedAt >= from);
        }
        if (to is not null)
        {
            query = query.Where(x => x.CreatedAt <= to);
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
            var loadedBackups = await query.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(cancellationToken);
            var loadedChildBackupIdsByBackupId = await ChildBackupIdsByBackupIdAsync(loadedBackups.Select(x => x.Id).ToList(), cancellationToken);
            return loadedBackups
                .Select(backup => BackupRestoreMapping.ToDto(
                    backup,
                    childBackupIds: loadedChildBackupIdsByBackupId.TryGetValue(backup.Id, out var loadedChildBackupIds) ? loadedChildBackupIds : []))
                .ToList();
        }

        var summaries = await query
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new BackupSummaryRow(
                x.Id,
                x.TriggerType,
                x.Status,
                x.BackupType,
                x.ContentMode,
                x.SourceClusterId,
                x.TargetId,
                x.PolicyId,
                x.ScheduleId,
                x.RequestedByUserId,
                x.RequestedByName,
                x.ManualRequestJson,
                x.StorageRootPath,
                x.ClickHouseBackupSettingsJson,
                x.CreatedAt,
                x.StartedAt,
                x.CompletedAt,
                x.Error,
                x.FailureReason,
                x.IsPinned,
                x.PinnedAt,
                x.PinnedByUserId,
                x.PinnedByName,
                x.DeletionReason,
                x.DeletionRequestedAt,
                x.DeletionStartedAt,
                x.DeletedAt,
                x.DeletionError,
                x.DeletionAttemptCount))
            .ToListAsync(cancellationToken);
        var summaryIds = summaries.Select(x => x.Id).ToList();
        var tableStats = summaryIds.Count == 0
            ? []
            : await db.BackupTables
                .AsNoTracking()
                .Where(x => summaryIds.Contains(x.BackupId))
                .GroupBy(x => x.BackupId)
                .Select(x => new BackupTableStats(x.Key, x.Count(), x.Where(t => t.BackupSizeBytes != null).Sum(t => t.BackupSizeBytes)))
                .ToListAsync(cancellationToken);
        var tableRelatedFullRows = summaryIds.Count == 0
            ? []
            : await db.BackupTables
                .AsNoTracking()
                .Where(x => summaryIds.Contains(x.BackupId) && x.ParentFullBackupId != null)
                .Select(x => new BackupRelatedFullRow(x.BackupId, x.ParentFullBackupId!.Value))
                .ToListAsync(cancellationToken);
        var shardRelatedFullRows = summaryIds.Count == 0
            ? []
            : await db.BackupTableShards
                .AsNoTracking()
                .Where(x => x.BackupTable != null && summaryIds.Contains(x.BackupTable.BackupId) && x.ParentFullBackupId != null)
                .Select(x => new BackupRelatedFullRow(x.BackupTable!.BackupId, x.ParentFullBackupId!.Value))
                .ToListAsync(cancellationToken);
        var relatedFullRows = tableRelatedFullRows.Concat(shardRelatedFullRows).Distinct().ToList();
        var statsByBackupId = tableStats.ToDictionary(x => x.BackupId);
        var relatedFullIdsByBackupId = relatedFullRows
            .GroupBy(x => x.BackupId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Guid>)x.Select(row => row.RelatedFullBackupId).Distinct().OrderBy(id => id).ToList());
        var childBackupIdsByBackupId = await ChildBackupIdsByBackupIdAsync(summaryIds, cancellationToken);
        return summaries
            .Select(x =>
            {
                statsByBackupId.TryGetValue(x.Id, out var stats);
                relatedFullIdsByBackupId.TryGetValue(x.Id, out var relatedFullBackupIds);
                childBackupIdsByBackupId.TryGetValue(x.Id, out var childBackupIds);
                return BackupRestoreMapping.ToSummaryDto(
                    x.Id,
                    x.TriggerType,
                    x.Status,
                    x.BackupType,
                    x.ContentMode,
                    x.SourceClusterId,
                    x.TargetId,
                    x.PolicyId,
                    x.ScheduleId,
                    x.RequestedByUserId,
                    x.RequestedByName,
                    x.ManualRequestJson,
                    x.StorageRootPath,
                    x.CreatedAt,
                    x.StartedAt,
                    x.CompletedAt,
                    x.Error,
                    x.FailureReason,
                    x.IsPinned,
                    x.PinnedAt,
                    x.PinnedByUserId,
                    x.PinnedByName,
                    x.DeletionReason,
                    x.DeletionRequestedAt,
                    x.DeletionStartedAt,
                    x.DeletedAt,
                    x.DeletionError,
                    x.DeletionAttemptCount,
                    stats?.TableCount ?? 0,
                    stats?.BackupSizeBytes,
                    relatedFullBackupIds ?? [],
                    childBackupIds ?? [],
                    x.ClickHouseBackupSettingsJson);
            })
            .ToList();
    }
    public async Task<BackupDto?> GetAsync(Guid id, bool includeTables = true, CancellationToken cancellationToken = default)
    {
        if (includeTables)
        {
            if (await LoadAsync(id, cancellationToken) is not { } backup)
            {
                return null;
            }

            var includedChildBackupIds = await DependentBackupIdsAsync(id, cancellationToken);
            return BackupRestoreMapping.ToDto(backup, childBackupIds: includedChildBackupIds);
        }

        var summary = await db.Backups
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { Backup = x, TableCount = x.Tables.Count, BackupSizeBytes = x.Tables.Where(t => t.BackupSizeBytes != null).Sum(t => t.BackupSizeBytes) })
            .FirstOrDefaultAsync(cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var relatedFullBackupIds = await RelatedFullBackupIdsAsync(id, cancellationToken);
        var childBackupIds = await DependentBackupIdsAsync(id, cancellationToken);
        return BackupRestoreMapping.ToDto(summary.Backup, summary.TableCount, summary.BackupSizeBytes, relatedFullBackupIds, childBackupIds, includeTables: false);
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
        await queueItems.CompleteOperationAsync(BackupRestoreQueueKind.Backup, backup.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var killResults = await KillBackupOperationsAsync(backup, cancellationToken);
        await audit.RecordAsync("canceled", AuditEntityType.Backup, id.ToString(), new { operationId = id, actor.UserId, actor.ActorName, killed = killResults.Killed, killFailures = killResults.Failures });
        return BackupRestoreMapping.ToDto(backup);
    }


    private static bool TryParseTableFilter(string? tableName, out string? database, out string table)
    {
        database = null;
        table = "";
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        var trimmed = tableName.Trim();
        var separator = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            table = trimmed;
            return true;
        }

        database = trimmed[..separator];
        table = trimmed[(separator + 1)..];
        return true;
    }
    private sealed record BackupSummaryRow(
        Guid Id,
        BackupTriggerType TriggerType,
        BackupRunStatus Status,
        BackupType BackupType,
        BackupContentMode ContentMode,
        Guid SourceClusterId,
        Guid? TargetId,
        Guid? PolicyId,
        Guid? ScheduleId,
        Guid? RequestedByUserId,
        string RequestedByName,
        string? ManualRequestJson,
        string? StorageRootPath,
        string? ClickHouseBackupSettingsJson,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        string? Error,
        string? FailureReason,
        bool IsPinned,
        DateTimeOffset? PinnedAt,
        Guid? PinnedByUserId,
        string? PinnedByName,
        string? DeletionReason,
        DateTimeOffset? DeletionRequestedAt,
        DateTimeOffset? DeletionStartedAt,
        DateTimeOffset? DeletedAt,
        string? DeletionError,
        int DeletionAttemptCount);

    private sealed record BackupTableStats(Guid BackupId, int TableCount, long? BackupSizeBytes);
    private sealed record BackupRelatedFullRow(Guid BackupId, Guid RelatedFullBackupId);
    private sealed record BackupChildRow(Guid ParentBackupId, Guid ChildBackupId);
    private async Task<(int Killed, IReadOnlyList<string> Failures)> KillBackupOperationsAsync(BackupEntity backup, CancellationToken cancellationToken)
    {
        if (backup.SourceCluster is null)
        {
            return (0, []);
        }

        var killed = 0;
        var failures = new List<string>();
        var operations = backup.Tables
            .SelectMany(table =>
            {
                var tableEndpoint = table.Shards
                    .OrderBy(shard => shard.SourceShardNumber)
                    .Select(shard => (ClickHouseNodeEndpoint?)new ClickHouseNodeEndpoint(shard.Host, shard.Port, shard.UseTls))
                    .FirstOrDefault();
                return table.Shards
                    .Where(shard => !string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
                    .Select(shard => new { Endpoint = (ClickHouseNodeEndpoint?)new ClickHouseNodeEndpoint(shard.Host, shard.Port, shard.UseTls), OperationId = shard.ClickHouseOperationId! })
                    .Concat(string.IsNullOrWhiteSpace(table.ClickHouseOperationId)
                        ? []
                        : [new { Endpoint = tableEndpoint, OperationId = table.ClickHouseOperationId! }]);
            })
            .DistinctBy(x => (EndpointKey(x.Endpoint), x.OperationId))
            .ToList();

        foreach (var operation in operations)
        {
            try
            {
                await KillBackupOperationOnceAsync(backup.SourceCluster, operation.Endpoint, operation.OperationId, cancellationToken);
                var retryDelay = options.CurrentValue.CancelKillRetryDelay;
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, cancellationToken);
                }
                await KillBackupOperationOnceAsync(backup.SourceCluster, operation.Endpoint, operation.OperationId, cancellationToken);

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

    private Task KillBackupOperationOnceAsync(ClickHouseClusterEntity cluster, ClickHouseNodeEndpoint? endpoint, string operationId, CancellationToken cancellationToken) =>
        endpoint is { } concreteEndpoint
            ? clickHouse.KillBackupRestoreOperationAsync(concreteEndpoint, cluster, operationId, cancellationToken)
            : clickHouse.KillBackupRestoreOperationAsync(cluster, operationId, cancellationToken);

    private static string EndpointKey(ClickHouseNodeEndpoint? endpoint) =>
        endpoint is null ? "default" : $"{endpoint.Host}:{endpoint.Port}:{endpoint.UseTls}";

    private async Task<IReadOnlyList<Guid>> RelatedFullBackupIdsAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var tableParents = await db.BackupTables
            .AsNoTracking()
            .Where(x => x.BackupId == backupId && x.ParentFullBackupId != null)
            .Select(x => x.ParentFullBackupId!.Value)
            .ToListAsync(cancellationToken);
        var shardParents = await db.BackupTableShards
            .AsNoTracking()
            .Where(x => x.BackupTable != null && x.BackupTable.BackupId == backupId && x.ParentFullBackupId != null)
            .Select(x => x.ParentFullBackupId!.Value)
            .ToListAsync(cancellationToken);
        return tableParents.Concat(shardParents).Distinct().OrderBy(x => x).ToList();
    }
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ChildBackupIdsByBackupIdAsync(IReadOnlyList<Guid> backupIds, CancellationToken cancellationToken)
    {
        if (backupIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        var tableChildren = await db.BackupTables
            .AsNoTracking()
            .Where(x => x.ParentFullBackupId != null && backupIds.Contains(x.ParentFullBackupId.Value))
            .Select(x => new BackupChildRow(x.ParentFullBackupId!.Value, x.BackupId))
            .ToListAsync(cancellationToken);
        var shardChildren = await db.BackupTableShards
            .AsNoTracking()
            .Where(x => x.BackupTable != null && x.ParentFullBackupId != null && backupIds.Contains(x.ParentFullBackupId.Value))
            .Select(x => new BackupChildRow(x.ParentFullBackupId!.Value, x.BackupTable!.BackupId))
            .ToListAsync(cancellationToken);
        return tableChildren
            .Concat(shardChildren)
            .GroupBy(x => x.ParentBackupId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Guid>)x.Select(row => row.ChildBackupId).Distinct().OrderBy(id => id).ToList());
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
