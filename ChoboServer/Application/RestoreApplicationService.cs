using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Serilog.Context;

namespace ChoboServer.Application;

public sealed class RestoreApplicationService(
    ChoboDbContext db,
    IBackupRestoreQueues queues,
    IClickHouseAdapter clickHouse,
    IAuditService audit,
    ActorContext actor,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<RestoreApplicationService>();

    public async Task<RestoreDto> InitiateAsync(InitiateRestoreRequest request, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == request.BackupId, cancellationToken);
        if (backup is null)
        {
            throw new ArgumentException("Backup was not found.");
        }
        if (backup.Status is BackupRunStatus.ManualDeleteRequested or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeleteRequested or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleteStarted or
            BackupRunStatus.BackupExpiredDeleted)
        {
            throw new ArgumentException("Deleted or delete-pending backups cannot be restored.");
        }
        if (backup.Status is not (BackupRunStatus.Succeeded or BackupRunStatus.PartiallySucceeded))
        {
            throw new ArgumentException("Only succeeded or partially succeeded backups can be restored.");
        }
        var targetCluster = await db.ClickHouseClusters.Include(x => x.AccessNodes).FirstOrDefaultAsync(x => x.Id == request.TargetClusterId && !x.IsDeleted, cancellationToken);
        if (targetCluster is null)
        {
            throw new ArgumentException("Target cluster was not found.");
        }

        var selected = SelectTables(backup.Tables, request);
        if (selected.Count == 0)
        {
            throw new ArgumentException("No backup tables match the restore request.");
        }
        if (request.SourceShard is <= 0 || request.TargetShard is <= 0)
        {
            throw new ArgumentException("Shard numbers must be positive.");
        }
        if (request.SourceShards is { Count: > 0 } sourceShards && sourceShards.Any(x => x <= 0))
        {
            throw new ArgumentException("Shard numbers must be positive.");
        }
        if (request.SourceShards is { Count: 0 })
        {
            throw new ArgumentException("SourceShards must not be empty when provided.");
        }
        if (request.SourceShard is not null && request.SourceShards is not null)
        {
            throw new ArgumentException("Use either SourceShard or SourceShards, not both.");
        }
        if (request.SourceShards is { Count: > 0 } requestedSourceShards && requestedSourceShards.Distinct().Count() != requestedSourceShards.Count)
        {
            throw new ArgumentException("SourceShards must not contain duplicates.");
        }
        if (request.TargetShards is { Count: > 0 } targetShards && targetShards.Any(x => x <= 0))
        {
            throw new ArgumentException("Shard numbers must be positive.");
        }
        if (request.TargetShards is { Count: 0 })
        {
            throw new ArgumentException("TargetShards must not be empty when provided.");
        }
        if (request.TargetShard is not null && request.TargetShards is not null)
        {
            throw new ArgumentException("Use either TargetShard or TargetShards, not both.");
        }
        if (request.TargetShards is { Count: > 0 } requestedTargetShards && requestedTargetShards.Distinct().Count() != requestedTargetShards.Count)
        {
            throw new ArgumentException("TargetShards must not contain duplicates.");
        }
        var layout = request.Layout ?? RestoreLayout.Preserve;
        if (request.TargetShards is not null && layout != RestoreLayout.Redistribute)
        {
            throw new ArgumentException("TargetShards can only be used with redistribute layout.");
        }
        if (selected.Count > 1 && request.Tables is null or { Count: 0 } && (!string.IsNullOrWhiteSpace(request.TargetDatabase) || !string.IsNullOrWhiteSpace(request.TargetTable)))
        {
            throw new ArgumentException("Target database/table overrides are supported only for a single table restore.");
        }

        var targetRepresentatives = SelectShardRepresentatives(await clickHouse.GetTopologyAsync(targetCluster, cancellationToken));

        var restore = new RestoreEntity
        {
            BackupId = request.BackupId,
            TargetClusterId = request.TargetClusterId,
            Append = request.Append,
            AllowSchemaMismatch = request.AllowSchemaMismatch,
            Layout = layout,
            SourceShard = request.SourceShard ?? (request.SourceShards?.Count == 1 ? request.SourceShards[0] : null),
            TargetShard = request.TargetShard,
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            RequestedByUserId = actor.UserId,
            RequestedByName = actor.ActorName
        };
        foreach (var (table, mapping) in selected)
        {
            var backupShards = table.Shards
                .Where(x => x.Status == BackupTableStatus.Succeeded)
                .Where(x => MatchesRequestedSourceShard(x.SourceShardNumber, request))
                .OrderBy(x => x.SourceShardNumber)
                .ToList();
            if (table.DataBackedUp && !request.SchemaOnly && backupShards.Count == 0)
            {
                throw new ArgumentException($"No succeeded backup shards match {table.Database}.{table.Table}.");
            }

            var restoreTable = new RestoreTableEntity
            {
                BackupTableId = table.Id,
                SourceDatabase = table.Database,
                SourceTable = table.Table,
                TargetDatabase = mapping?.TargetDatabase ?? request.TargetDatabase ?? table.Database,
                TargetTable = mapping?.TargetTable ?? request.TargetTable ?? table.Table,
                Append = mapping?.Append ?? request.Append,
                AllowSchemaMismatch = mapping?.AllowSchemaMismatch ?? request.AllowSchemaMismatch,
                SchemaOnly = mapping?.SchemaOnly ?? request.SchemaOnly
            };
            if (table.DataBackedUp && !restoreTable.SchemaOnly)
            {
                var shardPlans = PlanShardRestores(layout, backupShards, targetRepresentatives, request.TargetShard, request.TargetShards);
                var useTemporaryRestoreTables = restoreTable.Append || shardPlans.Count > 1;
                foreach (var plan in shardPlans)
                {
                    var shard = new RestoreTableShardEntity
                    {
                        BackupTableShardId = plan.BackupShard.Id,
                        SourceShardNumber = plan.BackupShard.SourceShardNumber,
                        TargetShardNumber = plan.Target?.ShardNumber,
                        TargetShardName = plan.Target?.ShardName,
                        TargetReplicaNumber = plan.Target?.ReplicaNumber,
                        TargetHost = plan.Endpoint.Host,
                        TargetPort = plan.Endpoint.Port,
                        TargetUseTls = plan.Endpoint.UseTls,
                        LayoutRole = plan.LayoutRole,
                        RestoreDatabase = restoreTable.TargetDatabase
                    };
                    shard.RestoreTableName = useTemporaryRestoreTables
                        ? $"__chobo_restore_{shard.Id:N}"
                        : restoreTable.TargetTable;
                    restoreTable.Shards.Add(shard);
                }
            }

            restore.Tables.Add(restoreTable);
        }

        using var operationCorrelationScope = OperationCorrelationContext.Push(restore.Id.ToString());
        using var operationLogScope = LogContext.PushProperty("OperationId", restore.Id.ToString());

        db.Restores.Add(restore);
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Restore {RestoreId} created by {ActorName} for backup {BackupId} into cluster {TargetClusterId} with {TableCount} table(s).", restore.Id, actor.ActorName, restore.BackupId, restore.TargetClusterId, restore.Tables.Count);
        await audit.RecordAsync("created", AuditEntityType.Restore, restore.Id.ToString(), new { operationId = restore.Id, restore.BackupId, restore.TargetClusterId, tableCount = restore.Tables.Count, shardCount = restore.Tables.Sum(x => x.Shards.Count), layout, request.SourceShard, request.SourceShards, request.TargetShard, request.TargetShards, request.SchemaOnly, tableOptions = restore.Tables.Select(x => new { x.SourceDatabase, x.SourceTable, x.TargetDatabase, x.TargetTable, x.Append, x.AllowSchemaMismatch, x.SchemaOnly }).ToList() });
        await queues.QueueRestoreAsync(restore.Id, cancellationToken);
        _logger.Information("Restore {RestoreId} queued.", restore.Id);
        await audit.RecordAsync("queued", AuditEntityType.Restore, restore.Id.ToString(), new { operationId = restore.Id, reason = "user" });
        return BackupRestoreMapping.ToDto(await LoadAsync(restore.Id, cancellationToken) ?? restore);
    }

    public async Task<IReadOnlyList<RestoreDto>> ListAsync(CancellationToken cancellationToken = default) =>
        (await db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(cancellationToken))
        .Select(BackupRestoreMapping.ToDto)
        .ToList();

    public async Task<RestoreDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await LoadAsync(id, cancellationToken) is { } restore ? BackupRestoreMapping.ToDto(restore) : null;

    private Task<RestoreEntity?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    private static IReadOnlyList<(BackupTableEntity Table, RestoreTableMappingRequest? Mapping)> SelectTables(IEnumerable<BackupTableEntity> tables, InitiateRestoreRequest request)
    {
        var available = tables.ToDictionary(x => x.Id);
        if (request.Tables is { Count: > 0 } mappings)
        {
            var selected = new List<(BackupTableEntity, RestoreTableMappingRequest?)>();
            foreach (var mapping in mappings)
            {
                if (!available.TryGetValue(mapping.BackupTableId, out var table))
                {
                    throw new ArgumentException($"Backup table {mapping.BackupTableId} was not found in the selected backup.");
                }

                selected.Add((table, NormalizeMapping(mapping, table)));
            }

            return selected;
        }

        return available.Values
            .Where(x => string.IsNullOrWhiteSpace(request.Database) || x.Database == request.Database)
            .Where(x => string.IsNullOrWhiteSpace(request.Table) || x.Table == request.Table)
            .Select(x => (x, (RestoreTableMappingRequest?)null))
            .ToList();
    }

    private static RestoreTableMappingRequest NormalizeMapping(RestoreTableMappingRequest mapping, BackupTableEntity table) =>
        mapping with
        {
            TargetDatabase = string.IsNullOrWhiteSpace(mapping.TargetDatabase) ? table.Database : mapping.TargetDatabase,
            TargetTable = string.IsNullOrWhiteSpace(mapping.TargetTable) ? table.Table : mapping.TargetTable
        };

    private static bool MatchesRequestedSourceShard(int sourceShardNumber, InitiateRestoreRequest request)
    {
        if (request.SourceShards is { Count: > 0 })
        {
            return request.SourceShards.Contains(sourceShardNumber);
        }

        return request.SourceShard is null || sourceShardNumber == request.SourceShard.Value;
    }

    private static IReadOnlyList<ClickHouseShardReplicaInfo> SelectShardRepresentatives(IReadOnlyList<ClickHouseShardReplicaInfo> topology) =>
        topology
            .GroupBy(x => x.ShardNumber)
            .OrderBy(x => x.Key)
            .Select(group => group
                .OrderBy(x => x.ErrorsCount)
                .ThenBy(x => x.ReplicaNumber)
                .ThenBy(x => x.Host, StringComparer.Ordinal)
                .ThenBy(x => x.Port)
                .First())
            .ToList();

    private static IReadOnlyList<ShardRestorePlan> PlanShardRestores(RestoreLayout layout, IReadOnlyList<BackupTableShardEntity> backupShards, IReadOnlyList<ClickHouseShardReplicaInfo> targetRepresentatives, int? requestedTargetShard, IReadOnlyList<int>? requestedTargetShards)
    {
        if (targetRepresentatives.Count == 0)
        {
            throw new ArgumentException("Target cluster has no restorable shards.");
        }

        if (requestedTargetShard is { } targetShard && !targetRepresentatives.Any(x => x.ShardNumber == targetShard))
        {
            throw new ArgumentException($"Target shard {targetShard} was not found.");
        }
        var targetPool = targetRepresentatives;
        if (requestedTargetShards is { Count: > 0 })
        {
            var missing = requestedTargetShards.Where(x => !targetRepresentatives.Any(t => t.ShardNumber == x)).Distinct().ToList();
            if (missing.Count > 0)
            {
                throw new ArgumentException($"Target shard {missing[0]} was not found.");
            }

            targetPool = targetRepresentatives.Where(x => requestedTargetShards.Contains(x.ShardNumber)).ToList();
        }

        var plans = new List<ShardRestorePlan>();
        for (var i = 0; i < backupShards.Count; i++)
        {
            var backupShard = backupShards[i];
            ClickHouseShardReplicaInfo? target;
            var role = layout.ToString();
            if (requestedTargetShard is { } fixedTargetShard)
            {
                target = targetRepresentatives.Single(x => x.ShardNumber == fixedTargetShard);
                role = "target-shard";
            }
            else if (layout == RestoreLayout.SingleNode)
            {
                target = targetRepresentatives[0];
            }
            else if (layout == RestoreLayout.Redistribute)
            {
                target = targetPool[i % targetPool.Count];
            }
            else
            {
                target = targetRepresentatives.FirstOrDefault(x => x.ShardNumber == backupShard.SourceShardNumber)
                    ?? throw new ArgumentException($"Preserve layout requires target shard {backupShard.SourceShardNumber}. Choose redistribute for different topologies.");
            }

            plans.Add(new ShardRestorePlan(backupShard, target, target.Endpoint, role));
        }

        return plans;
    }

    private sealed record ShardRestorePlan(BackupTableShardEntity BackupShard, ClickHouseShardReplicaInfo? Target, ClickHouseNodeEndpoint Endpoint, string LayoutRole);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

