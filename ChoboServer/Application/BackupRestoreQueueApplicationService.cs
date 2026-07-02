using System.Text.Json;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class BackupRestoreQueueApplicationService(
    ChoboDbContext db,
    IAuditService audit,
    ActorContext actor,
    IBackupRestoreConcurrencyCoordinator concurrency,
    IEndpointRewriteService endpointRewrites)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const long PositionStep = 1000;
    private const long MinimumQueuedPosition = 1000;

    public async Task EnsureBackupQueueItemsAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstAsync(x => x.Id == backupId, cancellationToken);
        var existingShardIds = await db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backupId)
            .Select(x => x.ShardId)
            .ToListAsync(cancellationToken);
        var existing = existingShardIds.ToHashSet();
        var position = await NextPositionAsync(cancellationToken);
        foreach (var table in backup.Tables.OrderBy(x => x.Database).ThenBy(x => x.Table))
        {
            foreach (var shard in table.Shards.OrderBy(x => x.SourceShardNumber).ThenBy(x => x.ReplicaNumber))
            {
                if (!existing.Add(shard.Id))
                {
                    continue;
                }
                db.BackupRestoreQueueItems.Add(new BackupRestoreQueueItemEntity
                {
                    Kind = BackupRestoreQueueKind.Backup,
                    Position = position,
                    OperationId = backup.Id,
                    TableId = table.Id,
                    ShardId = shard.Id,
                    ClusterId = backup.SourceClusterId,
                    LogicalShardNumber = shard.SourceShardNumber,
                    LogicalShardName = shard.SourceShardName,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                position += PositionStep;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task EnsureRestoreQueueItemsAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        var restore = await db.Restores
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstAsync(x => x.Id == restoreId, cancellationToken);
        var existingShardIds = await db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restoreId)
            .Select(x => x.ShardId)
            .ToListAsync(cancellationToken);
        var existing = existingShardIds.ToHashSet();
        var position = await NextPositionAsync(cancellationToken);
        foreach (var table in restore.Tables.OrderBy(x => x.TargetDatabase).ThenBy(x => x.TargetTable))
        {
            foreach (var shard in table.Shards.OrderBy(x => x.TargetShardNumber ?? int.MaxValue).ThenBy(x => x.SourceShardNumber))
            {
                if (!existing.Add(shard.Id))
                {
                    continue;
                }
                db.BackupRestoreQueueItems.Add(new BackupRestoreQueueItemEntity
                {
                    Kind = BackupRestoreQueueKind.Restore,
                    Position = position,
                    OperationId = restore.Id,
                    TableId = table.Id,
                    ShardId = shard.Id,
                    ClusterId = restore.TargetClusterId,
                    LogicalShardNumber = shard.TargetShardNumber ?? shard.SourceShardNumber,
                    LogicalShardName = shard.TargetShardName,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                position += PositionStep;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }


    public sealed record QueueWorkItem(Guid TableId, Guid ShardId, bool IsForced, bool IsResume = false);
    public sealed record QueueClaimResult(QueueWorkItem? WorkItem, bool HasQueuedWork);

    public async Task<QueueClaimResult> TryTakeNextBackupWorkAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups.AsNoTracking()
            .Include(x => x.SourceCluster)
            .Include(x => x.Target)
            .FirstAsync(x => x.Id == backupId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var runningItems = await db.BackupRestoreQueueItems.AsNoTracking()
            .Where(x => x.ClusterId == backup.SourceClusterId && x.StartedAt != null && x.CompletedAt == null)
            .Select(x => new { x.LogicalShardNumber })
            .ToListAsync(cancellationToken);
        var clusterRunning = runningItems.Count;
        var candidates = await db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backupId && x.StartedAt == null && x.CompletedAt == null)
            .OrderByDescending(x => x.IsForced)
            .ThenBy(x => x.Position)
            .Take(256)
            .ToListAsync(cancellationToken);
        var hasQueuedWork = false;
        foreach (var item in candidates)
        {
            var shard = await db.BackupTableShards.Include(x => x.BackupTable).FirstOrDefaultAsync(x => x.Id == item.ShardId, cancellationToken);
            if (shard is null)
            {
                continue;
            }
            if (shard.Status is not (BackupTableStatus.Queued or BackupTableStatus.Running))
            {
                continue;
            }
            hasQueuedWork = true;
            var isResume = shard.Status == BackupTableStatus.Running;
            var shardLimit = ShardMaxDop(backup.SourceCluster!, item.LogicalShardNumber, item.LogicalShardName);
            var shardRunning = runningItems.Count(x => x.LogicalShardNumber == item.LogicalShardNumber);
            var destinationKey = BuildBackupDestinationKey(backup.Target, shard.StoragePath);
            if (destinationKey is not null && await IsBackupDestinationBlockedAsync(item.ShardId, destinationKey, cancellationToken))
            {
                continue;
            }
            if (!concurrency.TryReserveQueueItem(
                    item.Kind,
                    item.OperationId,
                    item.ShardId,
                    backup.SourceClusterId,
                    item.LogicalShardNumber,
                    item.IsForced || isResume,
                    backup.SourceCluster!.BackupRestoreMaxDop,
                    shardLimit,
                    clusterRunning,
                    shardRunning,
                    destinationKey))
            {
                continue;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var destinationPath = NormalizeDestinationPath(shard.StoragePath);
                var claimQuery = db.BackupRestoreQueueItems
                    .Where(x => x.Id == item.Id && x.StartedAt == null && x.CompletedAt == null);
                if (destinationPath is not null)
                {
                    var backupTargetId = backup.TargetId;
                    claimQuery = claimQuery.Where(x =>
                        !db.BackupRestoreQueueItems
                            .Where(active => active.Kind == BackupRestoreQueueKind.Backup &&
                                             active.StartedAt != null &&
                                             active.CompletedAt == null &&
                                             active.ShardId != item.ShardId)
                            .Join(db.BackupTableShards,
                                active => active.ShardId,
                                activeShard => activeShard.Id,
                                (_, activeShard) => activeShard)
                            .Join(db.BackupTables,
                                activeShard => activeShard.BackupTableId,
                                activeTable => activeTable.Id,
                                (activeShard, activeTable) => new { activeShard, activeTable })
                            .Join(db.Backups,
                                active => active.activeTable.BackupId,
                                activeBackup => activeBackup.Id,
                                (active, activeBackup) => new { active.activeShard, activeBackup })
                            .Any(active => active.activeBackup.TargetId == backupTargetId && active.activeShard.StoragePath.Trim() == destinationPath) &&
                        !db.BackupTableShards
                            .Where(activeShard => activeShard.Id != item.ShardId &&
                                                  (activeShard.Status == BackupTableStatus.Queued || activeShard.Status == BackupTableStatus.Running) &&
                                                  activeShard.ClickHouseOperationId != null &&
                                                  activeShard.ClickHouseOperationId.Trim() != "" &&
                                                  activeShard.StoragePath.Trim() == destinationPath)
                            .Join(db.BackupTables,
                                activeShard => activeShard.BackupTableId,
                                activeTable => activeTable.Id,
                                (activeShard, activeTable) => new { activeShard, activeTable })
                            .Join(db.Backups,
                                active => active.activeTable.BackupId,
                                activeBackup => activeBackup.Id,
                                (active, activeBackup) => new { active.activeShard, activeBackup })
                            .Any(active => active.activeBackup.TargetId == backupTargetId));
                }

                var claimed = await claimQuery.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StartedAt, now), cancellationToken);
                if (claimed == 0)
                {
                    concurrency.ReleaseQueueItem(item.Kind, item.ShardId);
                    continue;
                }
                if (destinationKey is not null && await IsBackupDestinationBlockedAsync(item.ShardId, destinationKey, cancellationToken))
                {
                    await db.BackupRestoreQueueItems
                        .Where(x => x.Id == item.Id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.StartedAt, (DateTimeOffset?)null)
                            .SetProperty(x => x.NodeHost, (string?)null)
                            .SetProperty(x => x.NodePort, (int?)null)
                            .SetProperty(x => x.NodeUseTls, (bool?)null), cancellationToken);
                    concurrency.ReleaseQueueItem(item.Kind, item.ShardId);
                    continue;
                }
                item.StartedAt = now;
                shard.Status = BackupTableStatus.Running;
                shard.StartedAt ??= now;
                if (shard.BackupTable is { } table && table.Status == BackupTableStatus.Queued)
                {
                    table.Status = BackupTableStatus.Running;
                    table.StartedAt ??= now;
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                concurrency.ConfirmQueueItemStarted(item.Kind, item.ShardId);
                return new QueueClaimResult(new QueueWorkItem(item.TableId, item.ShardId, item.IsForced, isResume), true);
            }
            catch
            {
                concurrency.ReleaseQueueItem(item.Kind, item.ShardId);
                throw;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new QueueClaimResult(null, hasQueuedWork);
    }

    public async Task<QueueClaimResult> TryTakeNextRestoreWorkAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        var restore = await db.Restores.AsNoTracking()
            .Include(x => x.TargetCluster)
            .FirstAsync(x => x.Id == restoreId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var runningItems = await db.BackupRestoreQueueItems.AsNoTracking()
            .Where(x => x.ClusterId == restore.TargetClusterId && x.StartedAt != null && x.CompletedAt == null)
            .Select(x => new { x.LogicalShardNumber })
            .ToListAsync(cancellationToken);
        var clusterRunning = runningItems.Count;
        var candidates = await db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restoreId && x.StartedAt == null && x.CompletedAt == null)
            .OrderByDescending(x => x.IsForced)
            .ThenBy(x => x.Position)
            .Take(256)
            .ToListAsync(cancellationToken);
        var hasQueuedWork = false;
        foreach (var item in candidates)
        {
            var shard = await db.RestoreTableShards.Include(x => x.RestoreTable).FirstOrDefaultAsync(x => x.Id == item.ShardId, cancellationToken);
            if (shard is null)
            {
                continue;
            }
            if (shard.Status is not (RestoreTableStatus.Queued or RestoreTableStatus.Running))
            {
                continue;
            }
            hasQueuedWork = true;
            var isResume = shard.Status == RestoreTableStatus.Running;
            var shardLimit = ShardMaxDop(restore.TargetCluster!, item.LogicalShardNumber, item.LogicalShardName);
            var shardRunning = runningItems.Count(x => x.LogicalShardNumber == item.LogicalShardNumber);
            if (!concurrency.TryReserveQueueItem(
                    item.Kind,
                    item.OperationId,
                    item.ShardId,
                    restore.TargetClusterId,
                    item.LogicalShardNumber,
                    item.IsForced || isResume,
                    restore.TargetCluster!.BackupRestoreMaxDop,
                    shardLimit,
                    clusterRunning,
                    shardRunning,
                    null))
            {
                continue;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var claimed = await db.BackupRestoreQueueItems
                    .Where(x => x.Id == item.Id && x.StartedAt == null && x.CompletedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StartedAt, now), cancellationToken);
                if (claimed == 0)
                {
                    concurrency.ReleaseQueueItem(item.Kind, item.ShardId);
                    continue;
                }
                item.StartedAt = now;
                shard.Status = RestoreTableStatus.Running;
                shard.StartedAt ??= now;
                if (shard.RestoreTable is { } table && table.Status == RestoreTableStatus.Queued)
                {
                    table.Status = RestoreTableStatus.Running;
                    table.StartedAt ??= now;
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                concurrency.ConfirmQueueItemStarted(item.Kind, item.ShardId);
                return new QueueClaimResult(new QueueWorkItem(item.TableId, item.ShardId, item.IsForced, isResume), true);
            }
            catch
            {
                concurrency.ReleaseQueueItem(item.Kind, item.ShardId);
                throw;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new QueueClaimResult(null, hasQueuedWork);
    }
    public async Task<bool> IsNodeAvailableAsync(Guid clusterId, ClickHouseNodeEndpoint endpoint, bool force, CancellationToken cancellationToken = default)
    {
        if (force)
        {
            return true;
        }
        var cluster = await db.ClickHouseClusters.AsNoTracking().FirstAsync(x => x.Id == clusterId, cancellationToken);
        var limit = NodeMaxDop(cluster, endpoint);
        var running = await db.BackupRestoreQueueItems.AsNoTracking()
            .CountAsync(x => x.ClusterId == clusterId && x.StartedAt != null && x.CompletedAt == null && x.NodeHost == endpoint.Host && x.NodePort == endpoint.Port && x.NodeUseTls == endpoint.UseTls, cancellationToken);
        return running < limit;
    }
    public async Task<IReadOnlyList<BackupRestoreQueueItemDto>> ListAsync(BackupRestoreQueueKind kind = BackupRestoreQueueKind.All, string status = "active", int limit = 500, CancellationToken cancellationToken = default)
    {
        var normalizedStatus = NormalizeStatusFilter(status);
        var query = db.BackupRestoreQueueItems.AsNoTracking()
            .Where(x => kind == BackupRestoreQueueKind.All || x.Kind == kind);
        query = ApplyQueueStateFilter(query, normalizedStatus);

        var items = await query
            .OrderByDescending(x => x.StartedAt != null && x.CompletedAt == null)
            .ThenByDescending(x => x.IsForced)
            .ThenBy(x => x.Position)
            .Take(Math.Clamp(limit, 1, 10000))
            .ToListAsync(cancellationToken);
        var backupShardIds = items.Where(x => x.Kind == BackupRestoreQueueKind.Backup).Select(x => x.ShardId).ToList();
        var restoreShardIds = items.Where(x => x.Kind == BackupRestoreQueueKind.Restore).Select(x => x.ShardId).ToList();
        var backupShards = backupShardIds.Count == 0
            ? []
            : await db.BackupTableShards.AsNoTracking().Include(x => x.BackupTable).Where(x => backupShardIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var restoreShards = restoreShardIds.Count == 0
            ? []
            : await db.RestoreTableShards.AsNoTracking().Include(x => x.RestoreTable).Where(x => restoreShardIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);

        return items.Select(item => ToDto(item, backupShards, restoreShards))
            .Where(x => MatchesStatusFilter(x.Status, normalizedStatus))
            .ToList();
    }

    public async Task<BackupRestoreQueueItemDto?> MoveItemAsync(Guid id, MoveQueueItemRequest request, CancellationToken cancellationToken = default)
    {
        var item = await db.BackupRestoreQueueItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null)
        {
            return null;
        }
        await EnsureQueuedAsync(item, cancellationToken);
        var oldPosition = item.Position;
        item.Position = await CalculateMovePositionAsync(item.Position, request, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("queue-item-moved", item.Kind == BackupRestoreQueueKind.Backup ? AuditEntityType.BackupTableShard : AuditEntityType.RestoreTableShard, item.ShardId.ToString(), new { queueItemId = item.Id, item.Kind, oldPosition, item.Position, request.Direction, request.BeforeItemId });
        return await GetItemDtoAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupRestoreQueueItemDto>> MoveTableAsync(BackupRestoreQueueKind kind, Guid tableId, MoveQueueItemRequest request, CancellationToken cancellationToken = default)
    {
        if (kind is not (BackupRestoreQueueKind.Backup or BackupRestoreQueueKind.Restore))
        {
            throw new ArgumentException("Queue table moves require kind Backup or Restore.");
        }
        var items = await db.BackupRestoreQueueItems
            .Where(x => x.Kind == kind && x.TableId == tableId)
            .OrderBy(x => x.Position)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return [];
        }
        foreach (var item in items)
        {
            await EnsureQueuedAsync(item, cancellationToken);
        }
        var oldPositions = items.Select(x => new { x.Id, x.Position }).ToList();
        var firstNewPosition = await CalculateTableMovePositionAsync(items[0].Position, request, cancellationToken);
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Position = firstNewPosition;
        }
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("queue-table-moved", kind == BackupRestoreQueueKind.Backup ? AuditEntityType.BackupTable : AuditEntityType.RestoreTable, tableId.ToString(), new { kind, tableId, oldPositions, newPositions = items.Select(x => new { x.Id, x.Position }).ToList(), request.Direction, request.BeforeItemId });
        return (await ListAsync(kind, "all", 500, cancellationToken)).Where(x => x.TableId == tableId).ToList();
    }

    public async Task<IReadOnlyList<BackupRestoreQueueItemDto>> MoveOperationAsync(BackupRestoreQueueKind kind, Guid operationId, MoveQueueItemRequest request, CancellationToken cancellationToken = default)
    {
        if (kind is not (BackupRestoreQueueKind.Backup or BackupRestoreQueueKind.Restore))
        {
            throw new ArgumentException("Queue operation moves require kind Backup or Restore.");
        }

        var items = await db.BackupRestoreQueueItems
            .Where(x => x.Kind == kind && x.OperationId == operationId && x.StartedAt == null && x.CompletedAt == null)
            .OrderBy(x => x.Position)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return [];
        }

        var movable = new List<BackupRestoreQueueItemEntity>();
        foreach (var item in items)
        {
            if (await GetStatusAsync(item, cancellationToken) == BackupRestoreQueueStatus.Queued)
            {
                movable.Add(item);
            }
        }
        if (movable.Count == 0)
        {
            return [];
        }

        var oldPositions = movable.Select(x => new { x.Id, x.Position }).ToList();
        var firstNewPosition = await CalculateGroupMovePositionAsync(movable[0].Position, movable.Count, request, cancellationToken);
        for (var i = 0; i < movable.Count; i++)
        {
            movable[i].Position = firstNewPosition + (i * PositionStep);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("queue-operation-moved", kind == BackupRestoreQueueKind.Backup ? AuditEntityType.Backup : AuditEntityType.Restore, operationId.ToString(), new { kind, operationId, oldPositions, newPositions = movable.Select(x => new { x.Id, x.Position }).ToList(), request.Direction, request.BeforeItemId });
        return (await ListAsync(kind, "all", 10000, cancellationToken)).Where(x => x.OperationId == operationId).ToList();
    }
    public async Task<BackupRestoreQueueItemDto?> ForceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await db.BackupRestoreQueueItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null)
        {
            return null;
        }
        await EnsureQueuedAsync(item, cancellationToken);
        item.IsForced = true;
        item.ForcedAt = DateTimeOffset.UtcNow;
        item.ForcedByUserId = actor.UserId;
        item.ForcedByName = actor.ActorName;
        item.Position = await MinPositionAsync(cancellationToken) - PositionStep;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("queue-item-forced", item.Kind == BackupRestoreQueueKind.Backup ? AuditEntityType.BackupTableShard : AuditEntityType.RestoreTableShard, item.ShardId.ToString(), new { queueItemId = item.Id, item.Kind, item.OperationId, item.TableId, item.ShardId, item.ForcedByUserId, item.ForcedByName });
        return await GetItemDtoAsync(id, cancellationToken);
    }

    public async Task MarkStartedAsync(BackupRestoreQueueKind kind, Guid shardId, ClickHouseNodeEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var item = await db.BackupRestoreQueueItems.FirstOrDefaultAsync(x => x.Kind == kind && x.ShardId == shardId, cancellationToken);
        if (item is null)
        {
            return;
        }
        item.NodeHost = endpoint.Host;
        item.NodePort = endpoint.Port;
        item.NodeUseTls = endpoint.UseTls;
        item.StartedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(BackupRestoreQueueKind kind, Guid shardId, CancellationToken cancellationToken = default)
    {
        var item = await db.BackupRestoreQueueItems.FirstOrDefaultAsync(x => x.Kind == kind && x.ShardId == shardId, cancellationToken);
        if (item is null)
        {
            return;
        }
        item.CompletedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        concurrency.ReleaseQueueItem(kind, shardId);
    }

    public async Task ReleaseStartedAsync(BackupRestoreQueueKind kind, Guid shardId, CancellationToken cancellationToken = default)
    {
        await ClearStartedClaimAsync(kind, shardId, cancellationToken);
        concurrency.ReleaseQueueItem(kind, shardId);
    }

    public async Task ClearStartedClaimAsync(BackupRestoreQueueKind kind, Guid shardId, CancellationToken cancellationToken = default)
    {
        var item = await db.BackupRestoreQueueItems.FirstOrDefaultAsync(x => x.Kind == kind && x.ShardId == shardId, cancellationToken);
        if (item is null || item.CompletedAt is not null)
        {
            return;
        }
        item.StartedAt = null;
        item.NodeHost = null;
        item.NodePort = null;
        item.NodeUseTls = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public void ReleaseInMemoryClaim(BackupRestoreQueueKind kind, Guid shardId)
    {
        concurrency.ReleaseQueueItem(kind, shardId);
    }
    public async Task ResetIncompleteBackupClaimsAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backupId && x.StartedAt != null && x.CompletedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.StartedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.NodeHost, (string?)null)
                .SetProperty(x => x.NodePort, (int?)null)
                .SetProperty(x => x.NodeUseTls, (bool?)null), cancellationToken);
        concurrency.ReleaseOperation(BackupRestoreQueueKind.Backup, backupId);
    }

    public async Task ResetIncompleteBackupNodeClaimsAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup &&
                        x.OperationId == backupId &&
                        x.StartedAt != null &&
                        x.CompletedAt == null &&
                        x.NodeHost != null &&
                        x.NodePort != null &&
                        x.NodeUseTls != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.StartedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.NodeHost, (string?)null)
                .SetProperty(x => x.NodePort, (int?)null)
                .SetProperty(x => x.NodeUseTls, (bool?)null), cancellationToken);
        concurrency.ReleaseOperation(BackupRestoreQueueKind.Backup, backupId);
    }
    public async Task ResetIncompleteRestoreClaimsAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        await db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restoreId && x.StartedAt != null && x.CompletedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.StartedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.NodeHost, (string?)null)
                .SetProperty(x => x.NodePort, (int?)null)
                .SetProperty(x => x.NodeUseTls, (bool?)null), cancellationToken);
        concurrency.ReleaseOperation(BackupRestoreQueueKind.Restore, restoreId);
    }

    public async Task CompleteOperationAsync(BackupRestoreQueueKind kind, Guid operationId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.BackupRestoreQueueItems
            .Where(x => x.Kind == kind && x.OperationId == operationId && x.CompletedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CompletedAt, now), cancellationToken);
        concurrency.ReleaseOperation(kind, operationId);
    }

    public async Task<int> RemoveActiveOperationItemsAsync(BackupRestoreQueueKind kind, Guid operationId, string reason, CancellationToken cancellationToken = default)
    {
        if (kind is not (BackupRestoreQueueKind.Backup or BackupRestoreQueueKind.Restore))
        {
            throw new ArgumentException("Queue cleanup requires kind Backup or Restore.", nameof(kind));
        }

        var deleted = await db.BackupRestoreQueueItems
            .Where(x => x.Kind == kind && x.OperationId == operationId && x.CompletedAt == null)
            .ExecuteDeleteAsync(cancellationToken);
        concurrency.ReleaseOperation(kind, operationId);
        if (deleted > 0)
        {
            await audit.RecordAsync("queue-active-cleaned", kind == BackupRestoreQueueKind.Backup ? AuditEntityType.Backup : AuditEntityType.Restore, operationId.ToString(), new { kind, operationId, reason, deleted });
        }

        return deleted;
    }

    public async Task<int> RemoveInactiveOperationItemsAsync(string reason, CancellationToken cancellationToken = default)
    {
        var operations = await db.BackupRestoreQueueItems.AsNoTracking()
            .Where(x => x.CompletedAt == null)
            .GroupBy(x => new { x.Kind, x.OperationId })
            .Select(x => new { x.Key.Kind, x.Key.OperationId })
            .ToListAsync(cancellationToken);
        var deleted = 0;
        foreach (var operation in operations)
        {
            if (operation.Kind == BackupRestoreQueueKind.Backup)
            {
                var active = await db.Backups.AsNoTracking()
                    .AnyAsync(x => x.Id == operation.OperationId && (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running), cancellationToken);
                if (active)
                {
                    continue;
                }

                deleted += await RemoveActiveOperationItemsAsync(BackupRestoreQueueKind.Backup, operation.OperationId, reason, cancellationToken);
                continue;
            }

            if (operation.Kind == BackupRestoreQueueKind.Restore)
            {
                var active = await db.Restores.AsNoTracking()
                    .AnyAsync(x => x.Id == operation.OperationId && (x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running), cancellationToken);
                if (active)
                {
                    continue;
                }

                deleted += await RemoveActiveOperationItemsAsync(BackupRestoreQueueKind.Restore, operation.OperationId, reason, cancellationToken);
            }
        }

        return deleted;
    }

    public async Task<bool> TryReserveStartedNodeAsync(BackupRestoreQueueKind kind, Guid shardId, Guid clusterId, ClickHouseNodeEndpoint endpoint, bool force, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var cluster = await db.ClickHouseClusters.AsNoTracking().FirstAsync(x => x.Id == clusterId, cancellationToken);
        var limit = NodeMaxDop(cluster, endpoint);
        var running = await db.BackupRestoreQueueItems.AsNoTracking()
            .CountAsync(x => x.ClusterId == clusterId && x.StartedAt != null && x.CompletedAt == null && x.NodeHost == endpoint.Host && x.NodePort == endpoint.Port && x.NodeUseTls == endpoint.UseTls, cancellationToken);
        if (!concurrency.TryReserveNode(kind, shardId, clusterId, endpoint, force, limit, running))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        try
        {
            var updated = await db.BackupRestoreQueueItems
                .Where(x => x.Kind == kind && x.ShardId == shardId && x.StartedAt != null && x.CompletedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.NodeHost, endpoint.Host)
                    .SetProperty(x => x.NodePort, endpoint.Port)
                    .SetProperty(x => x.NodeUseTls, endpoint.UseTls), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (updated > 0)
            {
                concurrency.ConfirmNodeStarted(kind, shardId);
                return true;
            }

            concurrency.ReleaseNode(kind, shardId);
            return false;
        }
        catch
        {
            concurrency.ReleaseNode(kind, shardId);
            throw;
        }
    }

    private async Task EnsureQueuedAsync(BackupRestoreQueueItemEntity item, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(item, cancellationToken);
        if (status != BackupRestoreQueueStatus.Queued)
        {
            throw new ArgumentException("Only queued queue rows can be changed.");
        }
    }

    private async Task<BackupRestoreQueueStatus> GetStatusAsync(BackupRestoreQueueItemEntity item, CancellationToken cancellationToken)
    {
        if (item.Kind == BackupRestoreQueueKind.Backup)
        {
            var status = await db.BackupTableShards.Where(x => x.Id == item.ShardId).Select(x => x.Status).FirstAsync(cancellationToken);
            return status switch
            {
                BackupTableStatus.Queued => BackupRestoreQueueStatus.Queued,
                BackupTableStatus.Running => BackupRestoreQueueStatus.Running,
                BackupTableStatus.Succeeded => BackupRestoreQueueStatus.Succeeded,
                BackupTableStatus.PartiallySucceeded => BackupRestoreQueueStatus.PartiallySucceeded,
                BackupTableStatus.Failed => BackupRestoreQueueStatus.Failed,
                _ => BackupRestoreQueueStatus.Skipped
            };
        }
        var restoreStatus = await db.RestoreTableShards.Where(x => x.Id == item.ShardId).Select(x => x.Status).FirstAsync(cancellationToken);
        return restoreStatus switch
        {
            RestoreTableStatus.Queued => BackupRestoreQueueStatus.Queued,
            RestoreTableStatus.Running => BackupRestoreQueueStatus.Running,
            RestoreTableStatus.Succeeded => BackupRestoreQueueStatus.Succeeded,
            RestoreTableStatus.PartiallySucceeded => BackupRestoreQueueStatus.PartiallySucceeded,
            RestoreTableStatus.Failed => BackupRestoreQueueStatus.Failed,
            _ => BackupRestoreQueueStatus.Skipped
        };
    }

    private async Task<long> CalculateGroupMovePositionAsync(long currentPosition, int itemCount, MoveQueueItemRequest request, CancellationToken cancellationToken)
    {
        if (request.Direction == BackupRestoreQueueMoveDirection.Top && request.BeforeItemId is null)
        {
            return await MinQueuedPositionAsync(cancellationToken) - (Math.Max(1, itemCount) * PositionStep);
        }

        return await CalculateMovePositionAsync(currentPosition, request, cancellationToken);
    }
    private async Task<long> CalculateTableMovePositionAsync(long currentPosition, MoveQueueItemRequest request, CancellationToken cancellationToken)
    {
        if (request.Direction == BackupRestoreQueueMoveDirection.Top && request.BeforeItemId is null)
        {
            return await MinQueuedPositionAsync(cancellationToken) - 1;
        }

        return await CalculateMovePositionAsync(currentPosition, request, cancellationToken);
    }

    private async Task<long> CalculateMovePositionAsync(long currentPosition, MoveQueueItemRequest request, CancellationToken cancellationToken)
    {
        if (request.BeforeItemId is { } beforeItemId)
        {
            var before = await db.BackupRestoreQueueItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == beforeItemId, cancellationToken)
                ?? throw new ArgumentException("BeforeItemId was not found.");
            return before.Position - 1;
        }
        return request.Direction switch
        {
            BackupRestoreQueueMoveDirection.Top => await MinQueuedPositionAsync(cancellationToken) - 1,
            BackupRestoreQueueMoveDirection.Bottom => await MaxPositionAsync(cancellationToken) + PositionStep,
            BackupRestoreQueueMoveDirection.Up => await PreviousPositionAsync(currentPosition, cancellationToken) is { } previous ? previous - 1 : currentPosition,
            BackupRestoreQueueMoveDirection.Down => await NextGreaterPositionAsync(currentPosition, cancellationToken) is { } next ? next + 1 : currentPosition,
            _ => currentPosition
        };
    }

    private async Task<long> NextPositionAsync(CancellationToken cancellationToken) => Math.Max(MinimumQueuedPosition, await MaxPositionAsync(cancellationToken) + PositionStep);
    private async Task<long> MinQueuedPositionAsync(CancellationToken cancellationToken) => await db.BackupRestoreQueueItems.Where(x => x.StartedAt == null && x.CompletedAt == null).Select(x => (long?)x.Position).MinAsync(cancellationToken) ?? MinimumQueuedPosition;
    private async Task<long> MinPositionAsync(CancellationToken cancellationToken) => await db.BackupRestoreQueueItems.Select(x => (long?)x.Position).MinAsync(cancellationToken) ?? 0;
    private async Task<long> MaxPositionAsync(CancellationToken cancellationToken) => await db.BackupRestoreQueueItems.Select(x => (long?)x.Position).MaxAsync(cancellationToken) ?? 0;
    private async Task<long?> PreviousPositionAsync(long position, CancellationToken cancellationToken) => await db.BackupRestoreQueueItems.Where(x => x.Position < position).OrderByDescending(x => x.Position).Select(x => (long?)x.Position).FirstOrDefaultAsync(cancellationToken);
    private async Task<long?> NextGreaterPositionAsync(long position, CancellationToken cancellationToken) => await db.BackupRestoreQueueItems.Where(x => x.Position > position).OrderBy(x => x.Position).Select(x => (long?)x.Position).FirstOrDefaultAsync(cancellationToken);

    private async Task<BackupRestoreQueueItemDto?> GetItemDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.BackupRestoreQueueItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        if (item.Kind == BackupRestoreQueueKind.Backup)
        {
            var backupShard = await db.BackupTableShards.AsNoTracking()
                .Include(x => x.BackupTable)
                .FirstOrDefaultAsync(x => x.Id == item.ShardId, cancellationToken);
            var backupShards = backupShard is null
                ? new Dictionary<Guid, BackupTableShardEntity>()
                : new Dictionary<Guid, BackupTableShardEntity> { [backupShard.Id] = backupShard };
            return ToDto(item, backupShards, new Dictionary<Guid, RestoreTableShardEntity>());
        }

        var restoreShard = await db.RestoreTableShards.AsNoTracking()
            .Include(x => x.RestoreTable)
            .FirstOrDefaultAsync(x => x.Id == item.ShardId, cancellationToken);
        var restoreShards = restoreShard is null
            ? new Dictionary<Guid, RestoreTableShardEntity>()
            : new Dictionary<Guid, RestoreTableShardEntity> { [restoreShard.Id] = restoreShard };
        return ToDto(item, new Dictionary<Guid, BackupTableShardEntity>(), restoreShards);
    }

    private static string NormalizeStatusFilter(string status) =>
        status.Trim().ToLowerInvariant() switch
        {
            "" => "active",
            "active" => "active",
            "queued" => "queued",
            "running" => "running",
            "succeeded" => "succeeded",
            "partiallysucceeded" => "partially-succeeded",
            "partially-succeeded" => "partially-succeeded",
            "partial" => "partially-succeeded",
            "failed" => "failed",
            "skipped" => "skipped",
            "canceled" => "canceled",
            "all" => "all",
            _ => "active"
        };

    private static IQueryable<BackupRestoreQueueItemEntity> ApplyQueueStateFilter(IQueryable<BackupRestoreQueueItemEntity> query, string normalizedStatus) =>
        normalizedStatus switch
        {
            "active" => query.Where(x => x.CompletedAt == null),
            "queued" => query.Where(x => x.StartedAt == null && x.CompletedAt == null),
            "running" => query.Where(x => x.StartedAt != null && x.CompletedAt == null),
            _ => query
        };

    private static bool MatchesStatusFilter(BackupRestoreQueueStatus itemStatus, string normalizedStatus) =>
        normalizedStatus switch
        {
            "queued" => itemStatus == BackupRestoreQueueStatus.Queued,
            "running" => itemStatus == BackupRestoreQueueStatus.Running,
            "succeeded" => itemStatus == BackupRestoreQueueStatus.Succeeded,
            "partially-succeeded" => itemStatus == BackupRestoreQueueStatus.PartiallySucceeded,
            "failed" => itemStatus == BackupRestoreQueueStatus.Failed,
            "skipped" => itemStatus == BackupRestoreQueueStatus.Skipped,
            "canceled" => itemStatus == BackupRestoreQueueStatus.Canceled,
            "all" => true,
            _ => itemStatus is BackupRestoreQueueStatus.Queued or BackupRestoreQueueStatus.Running
        };


    private static int NodeMaxDop(ClickHouseClusterEntity cluster, ClickHouseNodeEndpoint endpoint)
    {
        foreach (var node in DeserializeNodeOverrides(cluster.NodeMaxDopOverridesJson))
        {
            if (node.Port == endpoint.Port && node.UseTls == endpoint.UseTls && string.Equals(node.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(1, node.MaxDop);
            }
        }
        return Math.Max(1, cluster.NodeMaxDopDefault);
    }

    private static int ShardMaxDop(ClickHouseClusterEntity cluster, int shardNumber, string? shardName)
    {
        foreach (var shard in DeserializeShardOverrides(cluster.ShardMaxDopOverridesJson))
        {
            if (shard.ShardNumber == shardNumber || (!string.IsNullOrWhiteSpace(shardName) && string.Equals(shard.ShardName, shardName, StringComparison.OrdinalIgnoreCase)))
            {
                return Math.Max(1, shard.MaxDop);
            }
        }
        return Math.Max(1, cluster.ShardMaxDopDefault);
    }

    private async Task<bool> IsBackupDestinationBlockedAsync(Guid shardId, string destinationKey, CancellationToken cancellationToken)
    {
        var activeDestinations = await (
            from activeShard in db.BackupTableShards.AsNoTracking()
            join activeTable in db.BackupTables.AsNoTracking() on activeShard.BackupTableId equals activeTable.Id
            join activeBackup in db.Backups.AsNoTracking() on activeTable.BackupId equals activeBackup.Id
            join activeTarget in db.BackupTargets.AsNoTracking() on activeBackup.TargetId equals activeTarget.Id
            where activeShard.Id != shardId &&
                  (
                      db.BackupRestoreQueueItems.Any(active => active.Kind == BackupRestoreQueueKind.Backup &&
                                                               active.ShardId == activeShard.Id &&
                                                               active.StartedAt != null &&
                                                               active.CompletedAt == null) ||
                      ((activeShard.Status == BackupTableStatus.Queued || activeShard.Status == BackupTableStatus.Running) &&
                       activeShard.ClickHouseOperationId != null &&
                       activeShard.ClickHouseOperationId.Trim() != "")
                  )
            select new
            {
                TargetId = (Guid?)activeTarget.Id,
                TargetType = activeTarget.Type,
                activeTarget.SettingsJson,
                activeShard.StoragePath
            }).ToListAsync(cancellationToken);

        return activeDestinations.Any(active => string.Equals(
            BuildBackupDestinationKey(active.TargetId, active.TargetType, active.SettingsJson, active.StoragePath),
            destinationKey,
            StringComparison.Ordinal));
    }

    private string? BuildBackupDestinationKey(BackupTargetEntity? target, string? storagePath) =>
        target is null
            ? BuildBackupDestinationKey(null, null, null, storagePath)
            : BuildBackupDestinationKey(target.Id, target.Type, target.SettingsJson, storagePath);

    private string? BuildBackupDestinationKey(Guid? targetId, string? targetType, string? targetSettingsJson, string? storagePath)
    {
        var normalizedPath = NormalizeDestinationPath(storagePath);
        if (normalizedPath is null)
        {
            return null;
        }

        if (string.Equals(targetType, StorageProviderTypes.S3, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(targetSettingsJson))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<S3TargetSettingsDto>(targetSettingsJson, JsonOptions);
                if (settings is not null)
                {
                    var destination = endpointRewrites.RewriteS3EndpointForClickHouse(S3TargetUrlBuilder.BuildObjectUrl(settings, normalizedPath));
                    return $"{StorageProviderTypes.S3}:{destination.AbsoluteUri}";
                }
            }
            catch
            {
                // Fall back to a conservative raw key when target settings are invalid or incomplete.
            }
        }

        return $"{targetType ?? "unknown"}:{targetId?.ToString("N") ?? "no-target"}:{normalizedPath.TrimStart('/')}";
    }

    private static string? NormalizeDestinationPath(string? storagePath) =>
        string.IsNullOrWhiteSpace(storagePath) ? null : storagePath.Trim();

    private static IReadOnlyList<ClusterNodeMaxDopOverrideDto> DeserializeNodeOverrides(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<ClusterNodeMaxDopOverrideDto>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static IReadOnlyList<ClusterShardMaxDopOverrideDto> DeserializeShardOverrides(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<ClusterShardMaxDopOverrideDto>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static BackupRestoreQueueItemDto ToDto(BackupRestoreQueueItemEntity item, IReadOnlyDictionary<Guid, BackupTableShardEntity> backupShards, IReadOnlyDictionary<Guid, RestoreTableShardEntity> restoreShards)
    {
        if (item.Kind == BackupRestoreQueueKind.Backup && backupShards.TryGetValue(item.ShardId, out var backupShard))
        {
            var table = backupShard.BackupTable!;
            var status = backupShard.Status switch
            {
                BackupTableStatus.Queued => BackupRestoreQueueStatus.Queued,
                BackupTableStatus.Running => BackupRestoreQueueStatus.Running,
                BackupTableStatus.Succeeded => BackupRestoreQueueStatus.Succeeded,
                BackupTableStatus.PartiallySucceeded => BackupRestoreQueueStatus.PartiallySucceeded,
                BackupTableStatus.Failed => BackupRestoreQueueStatus.Failed,
                _ => BackupRestoreQueueStatus.Skipped
            };
            return new(item.Id, item.Kind, status, item.Position, item.IsForced, item.ForcedAt, item.ForcedByUserId, item.ForcedByName, item.OperationId, item.TableId, item.ShardId, item.ClusterId, table.Database, table.Table, item.LogicalShardNumber, item.LogicalShardName, item.NodeHost ?? backupShard.Host, item.NodePort ?? backupShard.Port, item.NodeUseTls ?? backupShard.UseTls, backupShard.ClickHouseOperationId, backupShard.ClickHouseStatus, item.CreatedAt, item.StartedAt ?? backupShard.StartedAt, item.CompletedAt ?? backupShard.CompletedAt, null, backupShard.Error);
        }
        if (item.Kind == BackupRestoreQueueKind.Restore && restoreShards.TryGetValue(item.ShardId, out var restoreShard))
        {
            var table = restoreShard.RestoreTable!;
            var status = restoreShard.Status switch
            {
                RestoreTableStatus.Queued => BackupRestoreQueueStatus.Queued,
                RestoreTableStatus.Running => BackupRestoreQueueStatus.Running,
                RestoreTableStatus.Succeeded => BackupRestoreQueueStatus.Succeeded,
                RestoreTableStatus.PartiallySucceeded => BackupRestoreQueueStatus.PartiallySucceeded,
                RestoreTableStatus.Failed => BackupRestoreQueueStatus.Failed,
                _ => BackupRestoreQueueStatus.Skipped
            };
            return new(item.Id, item.Kind, status, item.Position, item.IsForced, item.ForcedAt, item.ForcedByUserId, item.ForcedByName, item.OperationId, item.TableId, item.ShardId, item.ClusterId, table.TargetDatabase, table.TargetTable, item.LogicalShardNumber, item.LogicalShardName, item.NodeHost ?? restoreShard.TargetHost, item.NodePort ?? restoreShard.TargetPort, item.NodeUseTls ?? restoreShard.TargetUseTls, restoreShard.ClickHouseOperationId, restoreShard.ClickHouseStatus, item.CreatedAt, item.StartedAt ?? restoreShard.StartedAt, item.CompletedAt ?? restoreShard.CompletedAt, null, restoreShard.Error);
        }
        return new(item.Id, item.Kind, BackupRestoreQueueStatus.Failed, item.Position, item.IsForced, item.ForcedAt, item.ForcedByUserId, item.ForcedByName, item.OperationId, item.TableId, item.ShardId, item.ClusterId, "unknown", "unknown", item.LogicalShardNumber, item.LogicalShardName, item.NodeHost, item.NodePort, item.NodeUseTls, null, null, item.CreatedAt, item.StartedAt, item.CompletedAt, null, "Queue target row is missing.");
    }
}
