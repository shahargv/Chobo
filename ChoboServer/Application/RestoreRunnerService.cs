using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace ChoboServer.Application;

public sealed class RestoreRunnerService(
    IServiceScopeFactory scopeFactory,
    ChoboDbContext db,
    IOptions<ChoboBackupRestoreOptions> options,
    BackupRestoreQueueApplicationService queue,
    IAuditService audit,
    Serilog.ILogger logger)
{
    private readonly Serilog.ILogger _logger = logger.ForContext<RestoreRunnerService>();

    public async Task RunAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        var restore = await db.Restores
            .Include(x => x.TargetCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == restoreId, cancellationToken);
        if (restore is null)
        {
            return;
        }

        using var operationCorrelationScope = OperationCorrelationContext.Push(restore.Id.ToString());
        using var operationLogScope = LogContext.PushProperty("OperationId", restore.Id.ToString());

        if (restore.Status is RestoreRunStatus.Succeeded or RestoreRunStatus.Failed or RestoreRunStatus.Canceled)
        {
            return;
        }

        try
        {
            _logger.Information("Starting restore run {RestoreId}. Current status: {Status}.", restore.Id, restore.Status);
            ValidateRestore(restore);
            restore.Status = RestoreRunStatus.Running;
            restore.StartedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await queue.EnsureRestoreQueueItemsAsync(restore.Id, cancellationToken);
            await queue.ResetIncompleteRestoreClaimsAsync(restore.Id, cancellationToken);
            await audit.RecordAsync("started", AuditEntityType.Restore, restore.Id.ToString(), new { restore.BackupId, restore.TargetClusterId });

            var maxDop = EffectiveMaxDop(restore.TargetCluster!);
            _logger.Information("Executing restore {RestoreId} with per-cluster maxdop {MaxDop} and {TableCount} table(s).", restore.Id, maxDop, restore.Tables.Count);
            await PrepareRestoreTablesAsync(restore.Id, cancellationToken);
            await RunRestoreShardWorkAsync(restore.Id, maxDop, cancellationToken);

            if (await db.Restores.Where(x => x.Id == restore.Id).Select(x => x.Status).FirstAsync(cancellationToken) == RestoreRunStatus.Canceled)
            {
                _logger.Information("Restore {RestoreId} observed cancellation and will not overwrite canceled status.", restore.Id);
                return;
            }

            await FinalizeRestoreTablesAsync(restore.Id, cancellationToken);
            db.ChangeTracker.Clear();
            restore = await db.Restores.Include(x => x.Tables).FirstAsync(x => x.Id == restoreId, cancellationToken);
            var statuses = restore.Tables.Select(x => x.Status).ToList();
            var tableCount = restore.Tables.Count;
            restore.Status = AggregateRestoreStatus(statuses);
            restore.CompletedAt = DateTimeOffset.UtcNow;
            restore.FailureReason = restore.Status is RestoreRunStatus.Failed or RestoreRunStatus.PartiallySucceeded
                ? await GetRestoreFailureReasonAsync(restore.Id, cancellationToken)
                : null;
            restore.Error = restore.FailureReason;
            await db.SaveChangesAsync(cancellationToken);
            LogRestoreCompletion(restore);
            var auditAction = restore.Status == RestoreRunStatus.Succeeded ? "succeeded" : restore.Status == RestoreRunStatus.PartiallySucceeded ? "partially-succeeded" : "failed";
            await audit.RecordAsync(auditAction, AuditEntityType.Restore, restore.Id.ToString(), new { tableCount, layout = restore.Layout, restore.SourceShard, restore.TargetShard, restore.FailureReason });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Restore {RestoreId} failed.", restoreId);
            var now = DateTimeOffset.UtcNow;
            restore.Status = RestoreRunStatus.Failed;
            restore.Error = ex.Message;
            restore.FailureReason = ex.Message;
            restore.CompletedAt = now;
            foreach (var table in restore.Tables.Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running))
            {
                table.Status = RestoreTableStatus.Failed;
                table.Error = ex.Message;
                table.CompletedAt ??= now;
                foreach (var shard in table.Shards.Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running))
                {
                    shard.Status = RestoreTableStatus.Failed;
                    shard.Error = ex.Message;
                    shard.CompletedAt ??= now;
                }
            }
            foreach (var item in db.BackupRestoreQueueItems.Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restore.Id && x.CompletedAt == null))
            {
                item.CompletedAt = now;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("failed", AuditEntityType.Restore, restore.Id.ToString(), new { error = ex.Message, restore.FailureReason });
        }
    }

    private void LogRestoreCompletion(RestoreEntity restore)
    {
        if (restore.Status is RestoreRunStatus.Failed or RestoreRunStatus.PartiallySucceeded)
        {
            _logger.Warning("Restore {RestoreId} finished with status {Status}. Failure reason: {FailureReason}.", restore.Id, restore.Status, restore.FailureReason);
            return;
        }

        _logger.Information("Restore {RestoreId} finished with status {Status}. Failure reason: {FailureReason}.", restore.Id, restore.Status, restore.FailureReason);
    }
    private enum RestoreShardRunResult
    {
        Completed,
        RetryLater
    }

    private async Task PrepareRestoreTablesAsync(Guid restoreId, CancellationToken cancellationToken)
    {
        var tableIdsByQueue = await db.BackupRestoreQueueItems.AsNoTracking()
            .Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restoreId)
            .GroupBy(x => x.TableId)
            .Select(x => new { TableId = x.Key, Position = x.Min(i => i.Position) })
            .ToDictionaryAsync(x => x.TableId, x => x.Position, cancellationToken);
        var tableIds = await db.RestoreTables.AsNoTracking()
            .Where(x => x.RestoreId == restoreId && (x.Status == RestoreTableStatus.Queued || x.Status == RestoreTableStatus.Running))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var tableId in tableIds.OrderBy(x => tableIdsByQueue.TryGetValue(x, out var position) ? position : long.MaxValue))
        {
            await PrepareRestoreTableAsync(restoreId, tableId, cancellationToken);
        }
    }

    private async Task PrepareRestoreTableAsync(Guid restoreId, Guid tableId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var scopedQueue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();

        var restore = await scopedDb.Restores
            .Include(x => x.TargetCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.Tables.Where(table => table.Id == tableId)).ThenInclude(x => x.Shards)
            .FirstAsync(x => x.Id == restoreId, cancellationToken);
        var table = restore.Tables.Single(x => x.Id == tableId);
        if (restore.Status == RestoreRunStatus.Canceled || table.Status is RestoreTableStatus.Succeeded or RestoreTableStatus.Failed or RestoreTableStatus.PartiallySucceeded or RestoreTableStatus.Skipped)
        {
            return;
        }
        var backupTable = await scopedDb.BackupTables.Include(x => x.SchemaDefinition).Include(x => x.Shards).FirstAsync(x => x.Id == table.BackupTableId, cancellationToken);

        table.Status = RestoreTableStatus.Running;
        table.StartedAt ??= DateTimeOffset.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);
        _logger.Information("Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} started.", table.Id, table.TargetDatabase, table.TargetTable);
        await scopedAudit.RecordAsync("table-started", AuditEntityType.RestoreTable, table.Id.ToString(), new { table.TargetDatabase, table.TargetTable });

        try
        {
            var shardPositions = await scopedDb.BackupRestoreQueueItems.AsNoTracking()
                .Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restore.Id && x.TableId == table.Id)
                .ToDictionaryAsync(x => x.ShardId, x => x.Position, cancellationToken);
            var orderedShards = table.Shards
                .OrderBy(x => shardPositions.TryGetValue(x.Id, out var position) ? position : long.MaxValue)
                .ThenBy(x => x.TargetShardNumber ?? int.MaxValue)
                .ThenBy(x => x.SourceShardNumber)
                .ToList();
            var hasSubmittedOperations = orderedShards.Any(x => !string.IsNullOrWhiteSpace(x.ClickHouseOperationId));
            var firstEndpoint = orderedShards.Count == 0
                ? new ClickHouseNodeEndpoint(restore.TargetCluster!.AccessNodes[0].Host, restore.TargetCluster.AccessNodes[0].Port, restore.TargetCluster.AccessNodes[0].UseTls)
                : new ClickHouseNodeEndpoint(orderedShards[0].TargetHost, orderedShards[0].TargetPort, orderedShards[0].TargetUseTls);
            await scopedClickHouse.ExecuteAsync(firstEndpoint, restore.TargetCluster!, $"CREATE DATABASE IF NOT EXISTS {ClickHouseSql.Identifier(table.TargetDatabase)}", cancellationToken);
            var existing = await scopedClickHouse.GetTableAsync(firstEndpoint, restore.TargetCluster!, table.TargetDatabase, table.TargetTable, cancellationToken);
            if (!hasSubmittedOperations && existing is not null && !table.Append)
            {
                throw new InvalidOperationException($"Target table {table.TargetDatabase}.{table.TargetTable} already exists.");
            }
            if (!hasSubmittedOperations && existing is not null && existing.SchemaHash != backupTable.SchemaDefinition!.SchemaHash)
            {
                if (!table.AllowSchemaMismatch)
                {
                    throw new InvalidOperationException($"Target table {table.TargetDatabase}.{table.TargetTable} has a different schema.");
                }

                table.Warning = "Target schema differs from backup schema; continuing because allow schema mismatch was requested.";
            }
            if (!hasSubmittedOperations && existing is null && table.Append)
            {
                throw new InvalidOperationException($"Append restore requires target table {table.TargetDatabase}.{table.TargetTable} to already exist.");
            }
            if (!hasSubmittedOperations && existing is null && !table.Append && restore.Layout != RestoreLayout.Redistribute)
            {
                await EnsureTargetTableExistsOnAllShardsAsync(scopedClickHouse, restore.TargetCluster!, backupTable, table, cancellationToken);
            }

            if (!backupTable.DataBackedUp || orderedShards.Count == 0)
            {
                if (existing is null)
                {
                    await scopedClickHouse.ExecuteAsync(firstEndpoint, restore.TargetCluster!, ClickHouseSql.RewriteCreateTableNameIfNotExists(backupTable.SchemaDefinition!.CreateTableSql, table.TargetDatabase, table.TargetTable), cancellationToken);
                }
                table.Status = RestoreTableStatus.Succeeded;
                table.ClickHouseStatus = "SCHEMA_ONLY";
                table.CompletedAt = DateTimeOffset.UtcNow;
                foreach (var shard in orderedShards)
                {
                    shard.Status = RestoreTableStatus.Succeeded;
                    shard.CompletedAt ??= DateTimeOffset.UtcNow;
                    await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Restore, shard.Id, cancellationToken);
                }
                await scopedDb.SaveChangesAsync(cancellationToken);
                _logger.Information("Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} completed as schema-only.", table.Id, table.TargetDatabase, table.TargetTable);
                await scopedAudit.RecordAsync("table-skipped", AuditEntityType.RestoreTable, table.Id.ToString(), new { reason = "schema-only", requested = orderedShards.Count == 0 && backupTable.DataBackedUp });
                return;
            }

            var usesTemporaryShardTables = orderedShards.Any(x => !string.Equals(x.RestoreTableName, table.TargetTable, StringComparison.Ordinal));
            if (!hasSubmittedOperations && existing is null && usesTemporaryShardTables)
            {
                await scopedClickHouse.ExecuteAsync(firstEndpoint, restore.TargetCluster!, ClickHouseSql.RewriteCreateTableNameIfNotExists(backupTable.SchemaDefinition!.CreateTableSql, table.TargetDatabase, table.TargetTable), cancellationToken);
            }
            await scopedDb.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} failed during preparation.", table.Id, table.TargetDatabase, table.TargetTable);
            table.Status = RestoreTableStatus.Failed;
            table.Error = ex.Message;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await FailPendingRestoreShardsAsync(table, ex.Message, scopedAudit);
            foreach (var shard in table.Shards)
            {
                await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Restore, shard.Id, CancellationToken.None);
            }
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("table-failed", AuditEntityType.RestoreTable, table.Id.ToString(), new { error = ex.Message });
        }
    }

    private async Task RunRestoreShardWorkAsync(Guid restoreId, int maxDop, CancellationToken cancellationToken)
    {
        var queuedWorkCount = await db.BackupRestoreQueueItems.AsNoTracking()
            .CountAsync(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restoreId && x.CompletedAt == null, cancellationToken);
        if (queuedWorkCount == 0)
        {
            return;
        }

        var forcedWorkCount = await db.BackupRestoreQueueItems.AsNoTracking()
            .CountAsync(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restoreId && x.StartedAt == null && x.CompletedAt == null && x.IsForced, cancellationToken);
        var retryCounts = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
        var failedEndpoints = new System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentDictionary<string, byte>>();
        var workerCount = Math.Min(queuedWorkCount, Math.Max(1, maxDop) + forcedWorkCount);
        var workers = Enumerable.Range(0, workerCount)
            .Select(async _ =>
            {
                while (true)
                {
                    BackupRestoreQueueApplicationService.QueueClaimResult claim;
                    using (var statusScope = scopeFactory.CreateScope())
                    {
                        var statusDb = statusScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
                        if (await IsRestoreCancellationTerminalAsync(statusDb, restoreId, cancellationToken))
                        {
                            _logger.Information("Restore {RestoreId} shard worker observed cancellation and stopped taking new shard work.", restoreId);
                            return;
                        }
                        var queue = statusScope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
                        claim = await queue.TryTakeNextRestoreWorkAsync(restoreId, cancellationToken);
                    }
                    if (claim.WorkItem is null)
                    {
                        if (!claim.HasQueuedWork)
                        {
                            return;
                        }
                        await Task.Delay(options.Value.PollInterval, cancellationToken);
                        continue;
                    }

                    var item = claim.WorkItem;
                    var result = await RunRestoreShardAsync(restoreId, item.TableId, item.ShardId, item.IsForced, retryCounts, failedEndpoints, cancellationToken);
                    if (result == RestoreShardRunResult.RetryLater)
                    {
                        await Task.Delay(options.Value.PollInterval, cancellationToken);
                    }
                }
            })
            .ToList();

        await Task.WhenAll(workers);
    }

    private static async Task<bool> IsRestoreCancellationTerminalAsync(ChoboDbContext scopedDb, Guid restoreId, CancellationToken cancellationToken)
    {
        var status = await scopedDb.Restores.Where(x => x.Id == restoreId).Select(x => x.Status).FirstAsync(cancellationToken);
        return status == RestoreRunStatus.Canceled;
    }

    private async Task<RestoreShardRunResult> RunRestoreShardAsync(Guid restoreId, Guid tableId, Guid shardId, bool isForcedQueueItem, System.Collections.Concurrent.ConcurrentDictionary<Guid, int> retryCounts, System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> failedEndpoints, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var scopedOptions = scope.ServiceProvider.GetRequiredService<IOptions<ChoboBackupRestoreOptions>>();
        var scopedQueue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
        var scopedTestHooks = scope.ServiceProvider.GetRequiredService<ITestHookCoordinator>();

        var restore = await scopedDb.Restores
            .Include(x => x.TargetCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.Tables.Where(table => table.Id == tableId)).ThenInclude(x => x.Shards)
            .FirstAsync(x => x.Id == restoreId, cancellationToken);
        var table = restore.Tables.Single();
        var shard = table.Shards.Single(x => x.Id == shardId);
        if (restore.Status == RestoreRunStatus.Canceled || shard.Status is RestoreTableStatus.Succeeded or RestoreTableStatus.Failed or RestoreTableStatus.PartiallySucceeded or RestoreTableStatus.Skipped)
        {
            return RestoreShardRunResult.Completed;
        }

        var backupTable = await scopedDb.BackupTables.Include(x => x.SchemaDefinition).Include(x => x.Shards).FirstAsync(x => x.Id == table.BackupTableId, cancellationToken);
        var backupShard = backupTable.Shards.Single(x => x.Id == shard.BackupTableShardId);
        var usesTemporaryShardTables = table.Shards.Any(x => !string.Equals(x.RestoreTableName, table.TargetTable, StringComparison.Ordinal));
        var endpoint = new ClickHouseNodeEndpoint(shard.TargetHost, shard.TargetPort, shard.TargetUseTls);

        if (string.IsNullOrWhiteSpace(shard.ClickHouseOperationId) && IsReplicatedMergeTreeEngine(backupTable.Engine))
        {
            var failedEndpointKeys = failedEndpoints.TryGetValue(shard.Id, out var failed) ? failed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            var candidates = (await scopedClickHouse.GetTopologyAsync(restore.TargetCluster!, cancellationToken))
                .Where(x => x.ShardNumber == (shard.TargetShardNumber ?? shard.SourceShardNumber))
                .OrderBy(x => failedEndpointKeys.Contains(EndpointKey(x.Endpoint)))
                .ThenBy(_ => Random.Shared.Next())
                .ToList();
            var selectedCandidateIndex = -1;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!await scopedQueue.TryReserveStartedNodeAsync(BackupRestoreQueueKind.Restore, shard.Id, restore.TargetClusterId, candidates[i].Endpoint, isForcedQueueItem, cancellationToken))
                {
                    continue;
                }
                selectedCandidateIndex = i;
                break;
            }
            if (selectedCandidateIndex < 0 && candidates.Count > 0)
            {
                await ReleaseRestoreShardForRetryAsync(scopedDb, scopedQueue, shard, cancellationToken);
                return RestoreShardRunResult.RetryLater;
            }
            if (selectedCandidateIndex >= 0)
            {
                var selected = candidates[selectedCandidateIndex];
                endpoint = selected.Endpoint;
                shard.TargetShardName = selected.ShardName;
                shard.TargetReplicaNumber = selected.ReplicaNumber;
                shard.TargetHost = selected.Host;
                shard.TargetPort = selected.Port;
                shard.TargetUseTls = selected.UseTls;
                await scopedDb.SaveChangesAsync(cancellationToken);
            }
            else if (!await scopedQueue.TryReserveStartedNodeAsync(BackupRestoreQueueKind.Restore, shard.Id, restore.TargetClusterId, endpoint, isForcedQueueItem, cancellationToken))
            {
                await ReleaseRestoreShardForRetryAsync(scopedDb, scopedQueue, shard, cancellationToken);
                return RestoreShardRunResult.RetryLater;
            }
        }

        try
        {
            await scopedQueue.MarkStartedAsync(BackupRestoreQueueKind.Restore, shard.Id, endpoint, cancellationToken);
            await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, $"CREATE DATABASE IF NOT EXISTS {ClickHouseSql.Identifier(shard.RestoreDatabase)}", cancellationToken);
            if (usesTemporaryShardTables)
            {
                await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, ClickHouseSql.RewriteCreateTableNameIfNotExists(backupTable.SchemaDefinition!.CreateTableSql, table.TargetDatabase, table.TargetTable), cancellationToken);
            }

            shard.Status = RestoreTableStatus.Running;
            shard.StartedAt ??= DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("shard-started", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber, targetNode = $"{shard.TargetHost}:{shard.TargetPort}", layout = restore.Layout, forced = isForcedQueueItem });

            if (!string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
            {
                var status = await scopedClickHouse.GetOperationStatusAsync(endpoint, restore.TargetCluster!, shard.ClickHouseOperationId, cancellationToken);
                if (status.Exists)
                {
                    if (!await PollRestoreShardAsync(scopedDb, restore.Id, scopedClickHouse, endpoint, restore.TargetCluster!, shard, status, scopedOptions.Value.PollInterval, cancellationToken))
                    {
                        return RestoreShardRunResult.Completed;
                    }
                }
                else
                {
                    throw MissingOperationException(shard.ClickHouseOperationId);
                }
            }

            if (string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
            {
                var operation = await scopedClickHouse.StartRestoreShardAsync(endpoint, restore.TargetCluster!, restore.Backup!.Target!, shard, backupTable, backupShard, cancellationToken);
                shard.ClickHouseOperationId = operation.OperationId;
                shard.ClickHouseStatus = operation.Status;
                table.ClickHouseOperationId ??= operation.OperationId;
                table.ClickHouseStatus = operation.Status;
                await scopedDb.SaveChangesAsync(cancellationToken);
                await scopedAudit.RecordAsync("clickhouse-operation-submitted", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { operation.OperationId, operation.Status, sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber });
                await scopedTestHooks.MaybeDelayRestoreBeforePollAsync(cancellationToken);
                if (!await PollRestoreShardAsync(scopedDb, restore.Id, scopedClickHouse, endpoint, restore.TargetCluster!, shard, new ClickHouseOperationStatus(true, operation.Status, null), scopedOptions.Value.PollInterval, cancellationToken))
                {
                    return RestoreShardRunResult.Completed;
                }
            }

            if (!string.Equals(shard.RestoreTableName, table.TargetTable, StringComparison.Ordinal))
            {
                var existing = await scopedClickHouse.GetTableAsync(endpoint, restore.TargetCluster!, table.TargetDatabase, table.TargetTable, cancellationToken);
                var insertSql = table.Warning is null
                    ? $"INSERT INTO {ClickHouseSql.Qualified(table.TargetDatabase, table.TargetTable)} SELECT * FROM {ClickHouseSql.Qualified(shard.RestoreDatabase, shard.RestoreTableName)}"
                    : BuildMismatchAppendInsertSql(existing!, backupTable.SchemaDefinition!, table.TargetDatabase, table.TargetTable, shard.RestoreTableName);
                await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, insertSql, cancellationToken);
                await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, $"DROP TABLE IF EXISTS {ClickHouseSql.Qualified(shard.RestoreDatabase, shard.RestoreTableName)}", cancellationToken);
            }

            shard.Status = RestoreTableStatus.Succeeded;
            shard.Error = null;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(cancellationToken);
            await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber, shard.ClickHouseStatus });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Restore, shard.Id, cancellationToken);
            return RestoreShardRunResult.Completed;
        }
        catch (Exception ex)
        {
            var maxRetries = Math.Max(0, scopedOptions.Value.TransientShardMaxRetries);
            var attempt = retryCounts.AddOrUpdate(shard.Id, 1, (_, current) => current + 1);
            if (attempt <= maxRetries && IsTransientShardFailure(ex, cancellationToken))
            {
                var retryDelay = scopedOptions.Value.TransientShardRetryDelay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : scopedOptions.Value.TransientShardRetryDelay;
                failedEndpoints.GetOrAdd(shard.Id, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)).TryAdd(EndpointKey(endpoint), 0);
                _logger.Warning(ex, "Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} shard {SourceShardNumber}->{TargetShardNumber} failed with a transient error; retry {RetryAttempt}/{MaxRetries} will be queued after {RetryDelay}.", table.Id, table.TargetDatabase, table.TargetTable, shard.SourceShardNumber, shard.TargetShardNumber, attempt, maxRetries, retryDelay);
                shard.Status = RestoreTableStatus.Queued;
                shard.Error = ex.Message;
                shard.ClickHouseOperationId = null;
                shard.ClickHouseStatus = null;
                shard.CompletedAt = null;
                shard.StartedAt = null;
                await scopedDb.SaveChangesAsync(CancellationToken.None);
                await scopedAudit.RecordAsync("shard-retry-scheduled", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { error = ex.Message, sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber, retryAttempt = attempt, maxRetries, retryDelaySeconds = retryDelay.TotalSeconds });
                await Task.Delay(retryDelay, CancellationToken.None);
                await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Restore, shard.Id, CancellationToken.None);
                return RestoreShardRunResult.RetryLater;
            }

            shard.Status = RestoreTableStatus.Failed;
            shard.Error = ex.Message;
            shard.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("shard-failed", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { error = ex.Message, sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber });
            await scopedQueue.MarkCompletedAsync(BackupRestoreQueueKind.Restore, shard.Id, CancellationToken.None);
            return RestoreShardRunResult.Completed;
        }
    }

    private static async Task ReleaseRestoreShardForRetryAsync(ChoboDbContext scopedDb, BackupRestoreQueueApplicationService scopedQueue, RestoreTableShardEntity shard, CancellationToken cancellationToken)
    {
        shard.Status = RestoreTableStatus.Queued;
        shard.StartedAt = null;
        await scopedDb.SaveChangesAsync(cancellationToken);
        await scopedQueue.ReleaseStartedAsync(BackupRestoreQueueKind.Restore, shard.Id, cancellationToken);
    }

    private async Task FinalizeRestoreTablesAsync(Guid restoreId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var tables = await scopedDb.RestoreTables.Include(x => x.Shards).Where(x => x.RestoreId == restoreId).ToListAsync(cancellationToken);
        foreach (var table in tables.Where(x => x.CompletedAt is null || x.Status == RestoreTableStatus.Running))
        {
            table.Status = AggregateRestoreTableStatus(table.Shards.Select(x => x.Status).ToList());
            table.ClickHouseStatus = table.Shards.Any(x => x.Status == RestoreTableStatus.Failed)
                ? "PARTIAL_OR_FAILED"
                : table.Shards.LastOrDefault(x => x.Status == RestoreTableStatus.Succeeded)?.ClickHouseStatus ?? table.ClickHouseStatus;
            table.Error = table.Status is RestoreTableStatus.Failed or RestoreTableStatus.PartiallySucceeded
                ? BuildRestoreShardFailureReason(table.Shards.Where(x => x.Status == RestoreTableStatus.Failed).ToList())
                : null;
            table.CompletedAt = DateTimeOffset.UtcNow;
            _logger.Information("Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} completed with ClickHouse status {Status}.", table.Id, table.TargetDatabase, table.TargetTable, table.ClickHouseStatus);
            var tableAction = table.Status == RestoreTableStatus.Succeeded ? "table-succeeded" : table.Status == RestoreTableStatus.PartiallySucceeded ? "table-partially-succeeded" : "table-failed";
            await scopedAudit.RecordAsync(tableAction, AuditEntityType.RestoreTable, table.Id.ToString(), new { table.TargetDatabase, table.TargetTable, shardCount = table.Shards.Count, failureReason = table.Error });
        }
        await scopedDb.SaveChangesAsync(cancellationToken);
    }
    private static async Task EnsureTargetTableExistsOnAllShardsAsync(IClickHouseAdapter adapter, ClickHouseClusterEntity cluster, BackupTableEntity backupTable, RestoreTableEntity table, CancellationToken cancellationToken)
    {
        var topology = await adapter.GetTopologyAsync(cluster, cancellationToken);
        var endpoints = SelectShardRepresentativeEndpoints(topology);
        if (endpoints.Count == 0 && cluster.AccessNodes.Count > 0)
        {
            endpoints = cluster.AccessNodes
                .Select(x => new ClickHouseNodeEndpoint(x.Host, x.Port, x.UseTls))
                .ToList();
        }

        var createTableSql = ClickHouseSql.RewriteCreateTableNameIfNotExists(backupTable.SchemaDefinition!.CreateTableSql, table.TargetDatabase, table.TargetTable);
        foreach (var endpoint in endpoints)
        {
            await adapter.ExecuteAsync(endpoint, cluster, $"CREATE DATABASE IF NOT EXISTS {ClickHouseSql.Identifier(table.TargetDatabase)}", cancellationToken);
            await adapter.ExecuteAsync(endpoint, cluster, createTableSql, cancellationToken);
        }
    }

    private static List<ClickHouseNodeEndpoint> SelectShardRepresentativeEndpoints(IReadOnlyList<ClickHouseShardReplicaInfo> topology) =>
        topology
            .GroupBy(x => x.ShardNumber)
            .OrderBy(x => x.Key)
            .Select(x =>
            {
                var representative = x.OrderBy(r => r.ErrorsCount).ThenBy(r => r.ReplicaNumber).First();
                return new ClickHouseNodeEndpoint(representative.Host, representative.Port, representative.UseTls);
            })
            .ToList();

    private static async Task FailPendingRestoreShardsAsync(RestoreTableEntity table, string error, IAuditService audit)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var shard in table.Shards.Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running))
        {
            shard.Status = RestoreTableStatus.Failed;
            shard.Error = error;
            shard.CompletedAt ??= now;
            await audit.RecordAsync("shard-failed", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { error, sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber });
        }
    }
    private static async Task PollRestoreAsync(IClickHouseAdapter adapter, ClickHouseClusterEntity cluster, RestoreTableEntity table, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!current.Exists)
            {
                throw MissingOperationException(table.ClickHouseOperationId);
            }

            table.ClickHouseStatus = current.Status;
            if (string.Equals(current.Status, "RESTORED", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (current.Status?.Contains("FAILED", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(current.Error) ? $"ClickHouse operation failed with status {current.Status}." : current.Error);
            }

            await Task.Delay(interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval, cancellationToken);
            current = await adapter.GetOperationStatusAsync(cluster, table.ClickHouseOperationId!, cancellationToken);
        }
    }

    private static async Task<bool> PollRestoreShardAsync(ChoboDbContext db, Guid restoreId, IClickHouseAdapter adapter, ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, RestoreTableShardEntity shard, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await db.Restores.Where(x => x.Id == restoreId).Select(x => x.Status).FirstAsync(cancellationToken) == RestoreRunStatus.Canceled)
            {
                return false;
            }
            if (!current.Exists)
            {
                throw MissingOperationException(shard.ClickHouseOperationId);
            }

            shard.ClickHouseStatus = current.Status;
            if (string.Equals(current.Status, "RESTORED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (current.Status?.Contains("FAILED", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(current.Error) ? $"ClickHouse operation failed with status {current.Status}." : current.Error);
            }

            await Task.Delay(interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval, cancellationToken);
            current = await adapter.GetOperationStatusAsync(endpoint, cluster, shard.ClickHouseOperationId!, cancellationToken);
        }
    }

    private void ValidateRestore(RestoreEntity restore)
    {
        if (restore.TargetCluster is null || restore.Backup?.Target is null)
        {
            throw new InvalidOperationException("Restore target cluster or backup target was not found.");
        }
    }


    private static bool IsReplicatedMergeTreeEngine(string engine) =>
        engine.Contains("Replicated", StringComparison.OrdinalIgnoreCase) && engine.Contains("MergeTree", StringComparison.OrdinalIgnoreCase);
    private int EffectiveMaxDop(ClickHouseClusterEntity cluster) =>
        Math.Max(1, cluster.BackupRestoreMaxDop > 0 ? cluster.BackupRestoreMaxDop : options.Value.MaxDop <= 0 ? 3 : options.Value.MaxDop);

    private async Task<string> GetRestoreFailureReasonAsync(Guid restoreId, CancellationToken cancellationToken)
    {
        var tableFailures = await db.RestoreTables
            .Where(x => x.RestoreId == restoreId && x.Status != RestoreTableStatus.Succeeded && x.Status != RestoreTableStatus.Skipped)
            .OrderBy(x => x.TargetDatabase)
            .ThenBy(x => x.TargetTable)
            .Select(x => new { x.TargetDatabase, x.TargetTable, x.Error })
            .ToListAsync(cancellationToken);
        var shardFailures = await db.RestoreTableShards
            .Where(x => x.RestoreTable!.RestoreId == restoreId && x.Status == RestoreTableStatus.Failed)
            .OrderBy(x => x.RestoreTable!.TargetDatabase)
            .ThenBy(x => x.RestoreTable!.TargetTable)
            .ThenBy(x => x.SourceShardNumber)
            .Select(x => new { x.RestoreTable!.TargetDatabase, x.RestoreTable.TargetTable, x.SourceShardNumber, x.TargetShardNumber, x.TargetHost, x.TargetPort, x.Error })
            .ToListAsync(cancellationToken);

        if (shardFailures.Count > 0)
        {
            return string.Join("; ", shardFailures.Select(x =>
            {
                var targetShard = x.TargetShardNumber is null ? "unknown" : x.TargetShardNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return $"Restore failed for {x.TargetDatabase}.{x.TargetTable} source shard {x.SourceShardNumber} to target shard {targetShard} on {x.TargetHost}:{x.TargetPort}: {NormalizeFailure(x.Error)}";
            }));
        }

        if (tableFailures.Count > 0)
        {
            return string.Join("; ", tableFailures.Select(x => $"Restore failed for {x.TargetDatabase}.{x.TargetTable}: {NormalizeFailure(x.Error)}"));
        }

        return "One or more restore tables or shards failed.";
    }

    private static string BuildMismatchAppendInsertSql(ClickHouseTableInfo existing, SchemaDefinitionEntity backupSchema, string database, string targetTable, string tempTable)
    {
        var sourceColumns = ReadColumnNames(backupSchema.ColumnsJson).ToHashSet(StringComparer.Ordinal);
        var targetColumns = ReadColumnNames(existing.ColumnsJson).Where(sourceColumns.Contains).ToList();
        if (targetColumns.Count == 0)
        {
            throw new InvalidOperationException("Target table has no columns in common with the restored table.");
        }

        var columnList = string.Join(", ", targetColumns.Select(ClickHouseSql.Identifier));
        return $"INSERT INTO {ClickHouseSql.Qualified(database, targetTable)} ({columnList}) SELECT {columnList} FROM {ClickHouseSql.Qualified(database, tempTable)}";
    }

    private static IReadOnlyList<string> ReadColumnNames(string columnsJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(columnsJson);
        return document.RootElement.EnumerateArray()
            .Select(x => x.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }

    private static string BuildRestoreShardFailureReason(IReadOnlyList<RestoreTableShardEntity> failedShards) =>
        failedShards.Count == 0
            ? "One or more shards failed."
            : string.Join("; ", failedShards
                .OrderBy(x => x.SourceShardNumber)
                .ThenBy(x => x.TargetShardNumber)
                .Select(x =>
                {
                    var targetShard = x.TargetShardNumber is null ? "unknown" : x.TargetShardNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return $"Source shard {x.SourceShardNumber} to target shard {targetShard} on {x.TargetHost}:{x.TargetPort}: {NormalizeFailure(x.Error)}";
                }));

    private static string NormalizeFailure(string? failure) =>
        string.IsNullOrWhiteSpace(failure) ? "No detailed failure was reported." : failure.Trim();

    private static string EndpointKey(ClickHouseNodeEndpoint endpoint) => $"{endpoint.Host}:{endpoint.Port}:{endpoint.UseTls}";

    private static bool IsTransientShardFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException || current is TaskCanceledException || current is System.Net.Http.HttpRequestException || current is System.Net.Sockets.SocketException)
            {
                return true;
            }

            var message = current.Message;
            if (IsStorageOrCredentialFailure(message))
            {
                return false;
            }

            if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("temporar", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("transient", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection timed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no route to host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("host not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStorageOrCredentialFailure(string message) =>
        message.Contains("S3_ERROR", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Aws::S3", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("BackupWriterS3", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("S3Exception", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("InvalidAccessKeyId", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("REQUIRED_PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException MissingOperationException(string? operationId) =>
        new($"ClickHouse operation {operationId ?? "(unknown)"} is missing from system.backups; its outcome is unknown.");

    private static RestoreRunStatus AggregateRestoreStatus(IReadOnlyList<RestoreTableStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return RestoreRunStatus.Succeeded;
        }
        if (statuses.All(x => x == RestoreTableStatus.Succeeded || x == RestoreTableStatus.Skipped))
        {
            return RestoreRunStatus.Succeeded;
        }
        if (statuses.Any(x => x == RestoreTableStatus.Succeeded || x == RestoreTableStatus.PartiallySucceeded || x == RestoreTableStatus.Skipped))
        {
            return RestoreRunStatus.PartiallySucceeded;
        }

        return RestoreRunStatus.Failed;
    }

    private static RestoreTableStatus AggregateRestoreTableStatus(IReadOnlyList<RestoreTableStatus> statuses)
    {
        if (statuses.Count == 0 || statuses.All(x => x == RestoreTableStatus.Succeeded || x == RestoreTableStatus.Skipped))
        {
            return RestoreTableStatus.Succeeded;
        }
        if (statuses.Any(x => x == RestoreTableStatus.Succeeded || x == RestoreTableStatus.PartiallySucceeded || x == RestoreTableStatus.Skipped))
        {
            return RestoreTableStatus.PartiallySucceeded;
        }

        return RestoreTableStatus.Failed;
    }
}
