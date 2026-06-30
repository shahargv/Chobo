using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace ChoboServer.Application;

public sealed class RestoreApplicationService(
    ChoboDbContext db,
    IBackupRestoreQueues queues,
    BackupRestoreQueueApplicationService queueItems,
    IClickHouseAdapter clickHouse,
    IOptionsMonitor<ChoboBackupRestoreOptions> options,
    IAuditService audit,
    ActorContext actor,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<RestoreApplicationService>();

    public async Task<ClickHouseSettingsPreviewDto> PreviewSettingsAsync(RestoreSettingsPreviewRequest request, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.BackupId, cancellationToken)
            ?? throw new ArgumentException("Backup was not found.");
        var targetCluster = await db.ClickHouseClusters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.TargetClusterId && !x.IsDeleted, cancellationToken)
            ?? throw new ArgumentException("Target cluster was not found.");
        BackupPolicyEntity? policy = null;
        if (backup.PolicyId is { } policyId)
        {
            policy = await db.BackupPolicies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == policyId && !x.IsDeleted, cancellationToken);
        }

        return ClickHouseAdvancedSettings.MergeWithSources(
            ("cluster", ClickHouseAdvancedSettings.Deserialize(targetCluster.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore)),
            ("policy", policy is null ? ClickHouseAdvancedSettings.Empty : ClickHouseAdvancedSettings.Deserialize(policy.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore)));
    }
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

        ValidateCreateTableSqlOverrides(request);
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
        var inheritedSettings = await PreviewSettingsAsync(new RestoreSettingsPreviewRequest(request.BackupId, request.TargetClusterId), cancellationToken);
        var effectiveSettings = request.ClickHouseRestoreSettings is null
            ? inheritedSettings.Settings
            : ClickHouseAdvancedSettings.Normalize(request.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore);

        var restore = new RestoreEntity
        {
            BackupId = request.BackupId,
            TargetClusterId = request.TargetClusterId,
            Append = request.Append,
            AllowSchemaMismatch = request.AllowSchemaMismatch,
            Layout = layout,
            SourceShard = request.SourceShard ?? (request.SourceShards?.Count == 1 ? request.SourceShards[0] : null),
            TargetShard = request.TargetShard,
            RequestJson = JsonSerializer.Serialize(request with { ClickHouseRestoreSettings = effectiveSettings }, JsonOptions),
            ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.SerializeNormalized(effectiveSettings),
            RequestedByUserId = actor.UserId,
            RequestedByName = actor.ActorName
        };
        foreach (var (table, mapping) in selected)
        {
            var backupShards = await ResolveRestoreSourceShardsAsync(table, mapping, request, cancellationToken);

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
            if (table.DataBackedUp && !restoreTable.SchemaOnly && backupShards.Count == 0)
            {
                throw new ArgumentException($"No succeeded backup shards match {table.Database}.{table.Table}.");
            }
            if (!restoreTable.SchemaOnly && backupShards.Count > 0)
            {
                if (layout == RestoreLayout.Preserve && request.SourceShard is null && request.SourceShards is null && request.TargetShard is null && request.TargetShards is null)
                {
                    var sourceShardCount = backupShards.Select(x => x.SourceShardNumber).Distinct().Count();
                    if (sourceShardCount != targetRepresentatives.Count)
                    {
                        throw new ArgumentException($"Preserve layout requires matching source and target shard counts. Source has {sourceShardCount}; target has {targetRepresentatives.Count}. Choose redistribute for different topologies.");
                    }
                }

                var shardPlans = PlanShardRestores(layout, backupShards, targetRepresentatives, request.TargetShard, request.TargetShards);
                var useTemporaryRestoreTables = restoreTable.Append && restoreTable.AllowSchemaMismatch;
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

        if (!request.ConfirmDestructive)
        {
            var destructiveReasons = await GetDestructiveRestoreReasonsAsync(restore, targetCluster, cancellationToken);
            if (destructiveReasons.Count > 0)
            {
                throw new ArgumentException($"This restore includes destructive actions and requires ConfirmDestructive=true. {string.Join(" ", destructiveReasons)}");
            }
        }

        using var operationCorrelationScope = OperationCorrelationContext.Push(restore.Id.ToString());
        using var operationLogScope = LogContext.PushProperty("OperationId", restore.Id.ToString());

        db.Restores.Add(restore);
        await db.SaveChangesAsync(cancellationToken);
        await queueItems.EnsureRestoreQueueItemsAsync(restore.Id, cancellationToken);
        _logger.Information("Restore {RestoreId} created by {ActorName} for backup {BackupId} into cluster {TargetClusterId} with {TableCount} table(s).", restore.Id, actor.ActorName, restore.BackupId, restore.TargetClusterId, restore.Tables.Count);
        await audit.RecordAsync("created", AuditEntityType.Restore, restore.Id.ToString(), new { operationId = restore.Id, restore.BackupId, restore.TargetClusterId, tableCount = restore.Tables.Count, shardCount = restore.Tables.Sum(x => x.Shards.Count), layout, request.SourceShard, request.SourceShards, request.TargetShard, request.TargetShards, request.SchemaOnly, tableOptions = restore.Tables.Select(x => new { x.SourceDatabase, x.SourceTable, x.TargetDatabase, x.TargetTable, x.Append, x.AllowSchemaMismatch, x.SchemaOnly, CreateTableSqlOverride = BuildCreateTableSqlOverrideAuditDetail(request, x.BackupTableId) }).ToList() });
        await queues.QueueRestoreAsync(restore.Id, cancellationToken);
        _logger.Information("Restore {RestoreId} queued.", restore.Id);
        await audit.RecordAsync("queued", AuditEntityType.Restore, restore.Id.ToString(), new { operationId = restore.Id, reason = "user" });
        return BackupRestoreMapping.ToDto(await LoadAsync(restore.Id, cancellationToken) ?? restore);
    }

    public async Task<IReadOnlyList<RestoreDto>> ListAsync(CancellationToken cancellationToken = default) =>
        (await db.Restores.AsNoTracking().Include(x => x.Tables).ThenInclude(x => x.Shards).ThenInclude(x => x.BackupTableShard).ThenInclude(x => x!.BackupTable).ThenInclude(x => x!.Backup).AsSplitQuery().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(cancellationToken))
        .Select(BackupRestoreMapping.ToDto)
        .ToList();

    public async Task<RestoreDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await LoadAsync(id, cancellationToken) is { } restore ? BackupRestoreMapping.ToDto(restore) : null;


    public async Task<EntityRestorePlanDto> PlanEntityRestoreAsync(EntityRestorePlanRequest request, CancellationToken cancellationToken = default)
    {
        var planned = await BuildEntityRestorePlanAsync(request, cancellationToken);
        return planned.Dto;
    }

    public async Task<RestoreDto> InitiateFromPlanAsync(EntityRestorePlanRequest request, CancellationToken cancellationToken = default)
    {
        var planned = await BuildEntityRestorePlanAsync(request, cancellationToken);
        var sourceMappingsByTableId = request.Tables?.ToDictionary(x => x.BackupTableId) ?? [];
        var tables = planned.Dto.Tables.Select(table =>
        {
            sourceMappingsByTableId.TryGetValue(table.BackupTableId, out var sourceMapping);
            return new RestoreTableMappingRequest(
                table.BackupTableId,
                table.TargetDatabase,
                table.TargetTable,
                table.Append,
                table.AllowSchemaMismatch,
                table.SchemaOnly,
                sourceMapping?.CreateTableSqlOverride,
                table.Shards.Select(shard => new RestoreShardSourceRequest(shard.SourceShardNumber, shard.BackupTableShardId)).ToList());
        }).ToList();

        var restoreRequest = new InitiateRestoreRequest(
            planned.AnchorBackupId,
            request.TargetClusterId,
            null,
            null,
            null,
            null,
            request.Append,
            request.AllowSchemaMismatch,
            request.Layout,
            request.SourceShard,
            request.TargetShard,
            tables,
            request.SchemaOnly,
            request.SourceShards,
            request.TargetShards,
            request.ConfirmDestructive,
            request.ClickHouseRestoreSettings);
        return await InitiateAsync(restoreRequest, cancellationToken);
    }
    public async Task<RestoreDto?> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var restore = await db.Restores
            .Include(x => x.TargetCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (restore is null)
        {
            return null;
        }
        if (restore.Status is not (RestoreRunStatus.Queued or RestoreRunStatus.Running))
        {
            throw new ArgumentException("Only queued or running restores can be canceled.");
        }

        using var operationCorrelationScope = OperationCorrelationContext.Push(restore.Id.ToString());
        using var operationLogScope = LogContext.PushProperty("OperationId", restore.Id.ToString());

        var now = DateTimeOffset.UtcNow;
        restore.Status = RestoreRunStatus.Canceled;
        restore.CompletedAt = now;
        restore.FailureReason = $"Restore canceled by {actor.ActorName}.";
        restore.Error = restore.FailureReason;
        foreach (var table in restore.Tables.Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running))
        {
            table.Status = RestoreTableStatus.Skipped;
            table.Error = "Restore canceled.";
            table.CompletedAt ??= now;
            foreach (var shard in table.Shards.Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running))
            {
                shard.Status = RestoreTableStatus.Skipped;
                shard.Error = "Restore canceled.";
                shard.CompletedAt ??= now;
            }
        }
        await queueItems.CompleteOperationAsync(BackupRestoreQueueKind.Restore, restore.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var killResults = await KillRestoreOperationsAsync(restore, cancellationToken);
        await audit.RecordAsync("canceled", AuditEntityType.Restore, id.ToString(), new { operationId = id, actor.UserId, actor.ActorName, killed = killResults.Killed, killFailures = killResults.Failures });
        return BackupRestoreMapping.ToDto(restore);
    }

    private async Task<IReadOnlyList<string>> GetDestructiveRestoreReasonsAsync(RestoreEntity restore, ClickHouseClusterEntity targetCluster, CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        foreach (var table in restore.Tables)
        {
            var tableName = $"{table.TargetDatabase}.{table.TargetTable}";
            if (table.AllowSchemaMismatch)
            {
                reasons.Add($"Schema mismatch is allowed for {tableName}.");
            }
            if (table.Append)
            {
                reasons.Add($"Data will be appended to existing table {tableName}.");
                continue;
            }
            if (table.SchemaOnly)
            {
                continue;
            }

            var endpoint = table.Shards.Count == 0
                ? new ClickHouseNodeEndpoint(targetCluster.AccessNodes[0].Host, targetCluster.AccessNodes[0].Port, targetCluster.AccessNodes[0].UseTls)
                : new ClickHouseNodeEndpoint(table.Shards[0].TargetHost, table.Shards[0].TargetPort, table.Shards[0].TargetUseTls);
            try
            {
                var existing = await clickHouse.GetTableAsync(endpoint, targetCluster, table.TargetDatabase, table.TargetTable, cancellationToken);
                if (existing is not null)
                {
                    reasons.Add($"Target table {tableName} already exists.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not check whether restore target table {TableName} already exists on {Host}:{Port}; restore initiation will continue and execution will report target connectivity failures.", tableName, endpoint.Host, endpoint.Port);
            }
        }

        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<(int Killed, IReadOnlyList<string> Failures)> KillRestoreOperationsAsync(RestoreEntity restore, CancellationToken cancellationToken)
    {
        if (restore.TargetCluster is null)
        {
            return (0, []);
        }

        var killed = 0;
        var failures = new List<string>();
        var operations = restore.Tables
            .SelectMany(table =>
            {
                var tableEndpoint = table.Shards
                    .OrderBy(shard => shard.TargetShardNumber ?? int.MaxValue)
                    .ThenBy(shard => shard.SourceShardNumber)
                    .Select(shard => (ClickHouseNodeEndpoint?)new ClickHouseNodeEndpoint(shard.TargetHost, shard.TargetPort, shard.TargetUseTls))
                    .FirstOrDefault();
                return table.Shards
                    .Where(shard => !string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
                    .Select(shard => new { Endpoint = (ClickHouseNodeEndpoint?)new ClickHouseNodeEndpoint(shard.TargetHost, shard.TargetPort, shard.TargetUseTls), OperationId = shard.ClickHouseOperationId! })
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
                await KillRestoreOperationOnceAsync(restore.TargetCluster, operation.Endpoint, operation.OperationId, cancellationToken);
                var retryDelay = options.CurrentValue.CancelKillRetryDelay;
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, cancellationToken);
                }
                await KillRestoreOperationOnceAsync(restore.TargetCluster, operation.Endpoint, operation.OperationId, cancellationToken);

                killed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{operation.OperationId}: {ex.Message}");
                _logger.Warning(ex, "Failed to kill ClickHouse restore operation {OperationId} for restore {RestoreId}.", operation.OperationId, restore.Id);
            }
        }

        return (killed, failures);
    }

    private Task KillRestoreOperationOnceAsync(ClickHouseClusterEntity cluster, ClickHouseNodeEndpoint? endpoint, string operationId, CancellationToken cancellationToken) =>
        endpoint is { } concreteEndpoint
            ? clickHouse.KillBackupRestoreOperationAsync(concreteEndpoint, cluster, operationId, cancellationToken)
            : clickHouse.KillBackupRestoreOperationAsync(cluster, operationId, cancellationToken);

    private static string EndpointKey(ClickHouseNodeEndpoint? endpoint) =>
        endpoint is null ? "default" : $"{endpoint.Host}:{endpoint.Port}:{endpoint.UseTls}";

    private Task<RestoreEntity?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).ThenInclude(x => x.BackupTableShard).ThenInclude(x => x!.BackupTable).ThenInclude(x => x!.Backup).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    private static void ValidateCreateTableSqlOverrides(InitiateRestoreRequest request)
    {
        foreach (var mapping in request.Tables ?? [])
        {
            if (mapping.CreateTableSqlOverride is null)
            {
                continue;
            }

            var sql = mapping.CreateTableSqlOverride.Trim();
            if (sql.Length == 0)
            {
                throw new ArgumentException("CreateTableSqlOverride must not be empty when provided.");
            }
            if (!IsSingleCreateTableStatement(sql))
            {
                throw new ArgumentException("CreateTableSqlOverride must be a single CREATE TABLE statement.");
            }
        }
    }

    private static bool IsSingleCreateTableStatement(string sql)
    {
        if (!sql.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var withoutTrailingSemicolon = sql.EndsWith(';') ? sql[..^1].TrimEnd() : sql;
        return !withoutTrailingSemicolon.Contains(';');
    }

    private static object? BuildCreateTableSqlOverrideAuditDetail(InitiateRestoreRequest request, Guid backupTableId)
    {
        var sql = request.Tables?.FirstOrDefault(x => x.BackupTableId == backupTableId)?.CreateTableSqlOverride;
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var normalized = sql.Trim();
        return new
        {
            present = true,
            length = normalized.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()
        };
    }
    private async Task<PlannedEntityRestore> BuildEntityRestorePlanAsync(EntityRestorePlanRequest request, CancellationToken cancellationToken)
    {
        if (request.PolicyId is null && request.AnchorBackupId is null)
        {
            throw new ArgumentException("Choose a policy or anchor backup.");
        }

        var anchor = request.AnchorBackupId is { } anchorId
            ? await db.Backups
                .Include(x => x.Tables).ThenInclude(x => x.Shards)
                .Include(x => x.Tables).ThenInclude(x => x.SchemaDefinition)
                .FirstOrDefaultAsync(x => x.Id == anchorId, cancellationToken)
            : await db.Backups
                .Include(x => x.Tables).ThenInclude(x => x.Shards)
                .Include(x => x.Tables).ThenInclude(x => x.SchemaDefinition)
                .Where(x => x.PolicyId == request.PolicyId && x.Tables.Any(t => t.SchemaDefinitionId != null))
                .Where(x => x.Status == BackupRunStatus.Succeeded || x.Status == BackupRunStatus.PartiallySucceeded)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        if (anchor is null)
        {
            throw new ArgumentException("Anchor backup was not found.");
        }
        if (!IsBackupRestorable(anchor.Status))
        {
            throw new ArgumentException("Only succeeded or partially succeeded backups can be used as restore anchors.");
        }
        var policyId = request.PolicyId ?? anchor.PolicyId ?? throw new ArgumentException("Entity restore requires a policy-backed anchor backup.");
        if (anchor.PolicyId != policyId)
        {
            throw new ArgumentException("Anchor backup does not belong to the selected policy.");
        }

        var targetCluster = await db.ClickHouseClusters.Include(x => x.AccessNodes).FirstOrDefaultAsync(x => x.Id == request.TargetClusterId && !x.IsDeleted, cancellationToken)
            ?? throw new ArgumentException("Target cluster was not found.");
        var initiateRequest = ToInitiateRequest(request, anchor.Id);
        ValidateCreateTableSqlOverrides(initiateRequest);
        var selected = SelectTables(anchor.Tables, initiateRequest);
        if (selected.Count == 0)
        {
            throw new ArgumentException("No anchor backup tables match the restore request.");
        }

        var targetRepresentatives = SelectShardRepresentatives(await clickHouse.GetTopologyAsync(targetCluster, cancellationToken));
        var layout = request.Layout ?? RestoreLayout.Preserve;
        var dtoTables = new List<RestorePlanTableDto>();
        foreach (var (table, rawMapping) in selected)
        {
            var mapping = rawMapping is null ? null : NormalizeMapping(rawMapping, table);
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

            var candidates = await LoadShardCandidatesAsync(policyId, anchor, table, request.AnchorBackupId is not null, cancellationToken);
            var defaults = ChooseDefaultShardSources(table, candidates, mapping);
            var selectedShards = defaults.Values
                .Where(x => MatchesRequestedSourceShard(x.SourceShardNumber, initiateRequest))
                .OrderBy(x => x.SourceShardNumber)
                .ToList();
            var shardDtos = new List<RestorePlanShardDto>();
            if (!restoreTable.SchemaOnly)
            {
                if (selectedShards.Count == 0)
                {
                    throw new ArgumentException($"No compatible backup shards match {table.Database}.{table.Table}.");
                }
                if (layout == RestoreLayout.Preserve && request.SourceShard is null && request.SourceShards is null && request.TargetShard is null && request.TargetShards is null)
                {
                    var sourceShardCount = selectedShards.Select(x => x.SourceShardNumber).Distinct().Count();
                    if (sourceShardCount != targetRepresentatives.Count)
                    {
                        throw new ArgumentException($"Preserve layout requires matching source and target shard counts. Source has {sourceShardCount}; target has {targetRepresentatives.Count}. Choose redistribute for different topologies.");
                    }
                }
                var plans = PlanShardRestores(layout, selectedShards, targetRepresentatives, request.TargetShard, request.TargetShards);
                foreach (var plan in plans)
                {
                    var sourceBackup = plan.BackupShard.BackupTable!.Backup!;
                    shardDtos.Add(new RestorePlanShardDto(
                        table.Id,
                        plan.BackupShard.Id,
                        sourceBackup.Id,
                        sourceBackup.BackupType,
                        sourceBackup.CreatedAt,
                        plan.BackupShard.SourceShardNumber,
                        plan.BackupShard.SourceShardName,
                        plan.Target?.ShardNumber,
                        plan.Target?.ShardName,
                        plan.Target?.ReplicaNumber,
                        plan.Endpoint.Host,
                        plan.Endpoint.Port,
                        plan.LayoutRole,
                        BuildRestoreStatementPreview(plan.BackupShard.BackupTable!, restoreTable, plan.BackupShard)));
                }
            }

            var candidateDtos = candidates
                .Select(candidate => new RestoreShardBackupCandidateDto(
                    candidate.BackupTable!.BackupId,
                    candidate.BackupTableId,
                    candidate.Id,
                    candidate.BackupTable.Backup!.BackupType,
                    candidate.BackupTable.Backup.Status,
                    candidate.BackupTable.Backup.CreatedAt,
                    candidate.SourceShardNumber,
                    candidate.SourceShardName,
                    candidate.Status,
                    IsShardCandidateCompatible(table, candidate),
                    defaults.TryGetValue(candidate.SourceShardNumber, out var selectedDefault) && selectedDefault.Id == candidate.Id,
                    IsShardCandidateCompatible(table, candidate) ? null : "Schema does not match the anchor table."))
                .OrderBy(x => x.SourceShardNumber)
                .ThenByDescending(x => x.CreatedAt)
                .ToList();
            dtoTables.Add(new RestorePlanTableDto(
                table.Id,
                table.Database,
                table.Table,
                restoreTable.TargetDatabase,
                restoreTable.TargetTable,
                restoreTable.Append,
                restoreTable.AllowSchemaMismatch,
                restoreTable.SchemaOnly,
                candidateDtos,
                shardDtos));
        }

        var queue = dtoTables.SelectMany(table => table.Shards.Select(shard => new RestorePlanQueueItemDto(
            table.BackupTableId,
            shard.BackupTableShardId,
            table.TargetDatabase,
            table.TargetTable,
            shard.TargetShardNumber ?? shard.SourceShardNumber,
            shard.TargetShardName,
            $"{shard.TargetHost}:{shard.TargetPort}",
            shard.RestoreStatement))).ToList();
        var replayRequest = request with
        {
            AnchorBackupId = anchor.Id,
            PolicyId = policyId,
            Database = null,
            Table = null,
            TargetDatabase = null,
            TargetTable = null,
            Tables = dtoTables.Select(table => new RestoreTableMappingRequest(
                table.BackupTableId,
                table.TargetDatabase,
                table.TargetTable,
                table.Append,
                table.AllowSchemaMismatch,
                table.SchemaOnly,
                request.Tables?.FirstOrDefault(x => x.BackupTableId == table.BackupTableId)?.CreateTableSqlOverride,
                table.Shards.Select(shard => new RestoreShardSourceRequest(shard.SourceShardNumber, shard.BackupTableShardId)).ToList())).ToList()
        };
        var cliJson = JsonSerializer.Serialize(replayRequest, JsonOptions);
        var cliCommand = "restore initiate-from-plan --file entity-restore-plan.json";
        var dto = new EntityRestorePlanDto(policyId, anchor.Id, request.TargetClusterId, layout, dtoTables, queue, cliCommand, cliJson);
        return new PlannedEntityRestore(anchor.Id, dto);
    }

    private static InitiateRestoreRequest ToInitiateRequest(EntityRestorePlanRequest request, Guid backupId) =>
        new(
            backupId,
            request.TargetClusterId,
            request.Database,
            request.Table,
            request.TargetDatabase,
            request.TargetTable,
            request.Append,
            request.AllowSchemaMismatch,
            request.Layout,
            request.SourceShard,
            request.TargetShard,
            request.Tables,
            request.SchemaOnly,
            request.SourceShards,
            request.TargetShards,
            request.ConfirmDestructive,
            request.ClickHouseRestoreSettings);

    private async Task<IReadOnlyList<BackupTableShardEntity>> ResolveRestoreSourceShardsAsync(BackupTableEntity anchorTable, RestoreTableMappingRequest? mapping, InitiateRestoreRequest request, CancellationToken cancellationToken)
    {
        if (mapping?.ShardSources is not { Count: > 0 } shardSources)
        {
            return anchorTable.Shards
                .Where(x => x.Status == BackupTableStatus.Succeeded)
                .Where(x => MatchesRequestedSourceShard(x.SourceShardNumber, request))
                .OrderBy(x => x.SourceShardNumber)
                .ToList();
        }
        if (shardSources.Select(x => x.SourceShardNumber).Distinct().Count() != shardSources.Count)
        {
            throw new ArgumentException("Shard source mappings must not contain duplicate source shard numbers.");
        }
        var ids = shardSources.Select(x => x.BackupTableShardId).ToList();
        var selected = await db.BackupTableShards
            .Include(x => x.BackupTable).ThenInclude(x => x!.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.BackupTable).ThenInclude(x => x!.SchemaDefinition)
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (selected.Count != ids.Count)
        {
            throw new ArgumentException("One or more shard source backups were not found.");
        }
        foreach (var source in shardSources)
        {
            var shard = selected.Single(x => x.Id == source.BackupTableShardId);
            ValidateShardSource(anchorTable, shard, source.SourceShardNumber);
        }
        return selected
            .Where(x => MatchesRequestedSourceShard(x.SourceShardNumber, request))
            .OrderBy(x => x.SourceShardNumber)
            .ToList();
    }

    private async Task<List<BackupTableShardEntity>> LoadShardCandidatesAsync(Guid policyId, BackupEntity anchor, BackupTableEntity anchorTable, bool startedFromAnchorBackup, CancellationToken cancellationToken)
    {
        var query = db.BackupTableShards
            .Include(x => x.BackupTable).ThenInclude(x => x!.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.BackupTable).ThenInclude(x => x!.SchemaDefinition)
            .Where(x => x.Status == BackupTableStatus.Succeeded)
            .Where(x => x.BackupTable != null && (x.BackupTable.Status == BackupTableStatus.Succeeded || x.BackupTable.Status == BackupTableStatus.PartiallySucceeded) && x.BackupTable.DataBackedUp)
            .Where(x => x.BackupTable!.Backup != null && x.BackupTable.Backup.PolicyId == policyId)
            .Where(x => x.BackupTable!.Backup!.Status == BackupRunStatus.Succeeded || x.BackupTable.Backup.Status == BackupRunStatus.PartiallySucceeded)
            .Where(x => x.BackupTable!.Database == anchorTable.Database && x.BackupTable.Table == anchorTable.Table)
            .Where(x => x.BackupTable!.Backup!.TargetId != null)
            ;
        if (startedFromAnchorBackup)
        {
            query = query.Where(x => x.BackupTable!.Backup!.CreatedAt <= anchor.CreatedAt);
        }
        return await query.OrderBy(x => x.SourceShardNumber).ThenByDescending(x => x.BackupTable!.Backup!.CreatedAt).ToListAsync(cancellationToken);
    }

    private static Dictionary<int, BackupTableShardEntity> ChooseDefaultShardSources(BackupTableEntity anchorTable, IReadOnlyList<BackupTableShardEntity> candidates, RestoreTableMappingRequest? mapping)
    {
        var defaults = candidates
            .Where(candidate => IsShardCandidateCompatible(anchorTable, candidate))
            .GroupBy(x => x.SourceShardNumber)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.BackupTable!.Backup!.CreatedAt).First());
        foreach (var source in mapping?.ShardSources ?? [])
        {
            var shard = candidates.FirstOrDefault(x => x.Id == source.BackupTableShardId)
                ?? throw new ArgumentException($"Shard source {source.BackupTableShardId} was not found in the available candidates.");
            ValidateShardSource(anchorTable, shard, source.SourceShardNumber);
            defaults[source.SourceShardNumber] = shard;
        }
        return defaults;
    }

    private static void ValidateShardSource(BackupTableEntity anchorTable, BackupTableShardEntity shard, int requestedSourceShard)
    {
        if (shard.SourceShardNumber != requestedSourceShard)
        {
            throw new ArgumentException($"Shard source {shard.Id} is for source shard {shard.SourceShardNumber}, not {requestedSourceShard}.");
        }
        if (shard.Status != BackupTableStatus.Succeeded || shard.BackupTable is null || shard.BackupTable.Status is not (BackupTableStatus.Succeeded or BackupTableStatus.PartiallySucceeded) || shard.BackupTable.Backup is null || !IsBackupRestorable(shard.BackupTable.Backup.Status) || IsDeletedOrDeletePendingBackupStatus(shard.BackupTable.Backup.Status))
        {
            throw new ArgumentException($"Shard source {shard.Id} is not restorable.");
        }
        if (anchorTable.Backup is { } anchorBackup)
        {
            if (shard.BackupTable.Backup.PolicyId != anchorBackup.PolicyId || shard.BackupTable.Backup.SourceClusterId != anchorBackup.SourceClusterId)
            {
                throw new ArgumentException($"Shard source {shard.Id} does not belong to the same policy and source cluster as the anchor backup.");
            }
        }
        if (shard.BackupTable.Database != anchorTable.Database || shard.BackupTable.Table != anchorTable.Table)
        {
            throw new ArgumentException($"Shard source {shard.Id} does not match anchor table {anchorTable.Database}.{anchorTable.Table}.");
        }
        if (!IsShardCandidateCompatible(anchorTable, shard))
        {
            throw new ArgumentException($"Shard source {shard.Id} schema does not match the anchor table.");
        }
    }

    private static bool IsShardCandidateCompatible(BackupTableEntity anchorTable, BackupTableShardEntity shard) =>
        anchorTable.SchemaDefinition?.SchemaHash is { } anchorHash && shard.BackupTable?.SchemaDefinition?.SchemaHash == anchorHash;

    private static string BuildRestoreStatementPreview(BackupTableEntity backupTable, RestoreTableEntity restoreTable, BackupTableShardEntity shard)
    {
        var source = RestoreSourcePreview(backupTable.Backup?.Target, shard.StoragePath);
        return $"RESTORE TABLE {ClickHouseSql.Qualified(backupTable.Database, backupTable.Table)} AS {ClickHouseSql.Qualified(restoreTable.TargetDatabase, restoreTable.TargetTable)} FROM {source} ASYNC -- source backup {backupTable.BackupId}, shard {shard.SourceShardNumber}";
    }

    private static string RestoreSourcePreview(BackupTargetEntity? target, string storagePath)
    {
        if (target is null || !string.Equals(target.Type, StorageProviderTypes.S3, StringComparison.OrdinalIgnoreCase))
        {
            return $"<backup-target:{target?.Id.ToString() ?? "unknown"}>/{RedactCredentialLikeParts(storagePath)}";
        }

        var settings = string.IsNullOrWhiteSpace(target.SettingsJson)
            ? new S3TargetSettingsDto("", "us-east-1", "", null, true)
            : JsonSerializer.Deserialize<S3TargetSettingsDto>(target.SettingsJson, JsonOptions) ?? new S3TargetSettingsDto("", "us-east-1", "", null, true);
        var endpoint = RedactCredentialLikeParts(S3TargetUrlBuilder.BuildObjectUrl(settings, storagePath).ToString());
        return ClickHouseSql.S3(endpoint, "REDACTED", "REDACTED");
    }

    private static string RedactCredentialLikeParts(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var builder = new UriBuilder(uri) { UserName = string.IsNullOrEmpty(uri.UserInfo) ? "" : "REDACTED", Password = "" };
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            var query = builder.Query.TrimStart('?');
            var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var separator = part.IndexOf('=', StringComparison.Ordinal);
                    var name = separator >= 0 ? part[..separator] : part;
                    return IsCredentialQueryName(Uri.UnescapeDataString(name)) ? $"{name}=REDACTED" : part;
                });
            builder.Query = string.Join("&", parts);
        }
        return builder.Uri.ToString();
    }

    private static bool IsCredentialQueryName(string name) =>
        name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackupRestorable(BackupRunStatus status) =>
        status is BackupRunStatus.Succeeded or BackupRunStatus.PartiallySucceeded;

    private static bool IsDeletedOrDeletePendingBackupStatus(BackupRunStatus status) =>
        status is BackupRunStatus.ManualDeleteRequested or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeleteRequested or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleteStarted or
            BackupRunStatus.BackupExpiredDeleted;
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
    private sealed record PlannedEntityRestore(Guid AnchorBackupId, EntityRestorePlanDto Dto);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
