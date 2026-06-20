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
            await audit.RecordAsync("started", AuditEntityType.Restore, restore.Id.ToString(), new { restore.BackupId, restore.TargetClusterId });

            var maxDop = EffectiveMaxDop(restore.TargetCluster!);
            _logger.Information("Executing restore {RestoreId} with effective maxdop {MaxDop} and {TableCount} table(s).", restore.Id, maxDop, restore.Tables.Count);
            using var semaphore = new SemaphoreSlim(maxDop, maxDop);
            var tasks = restore.Tables
                .Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running)
                .Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try { await RunTableAsync(restore.Id, table.Id, cancellationToken); }
                    finally { semaphore.Release(); }
                })
                .ToList();
            await Task.WhenAll(tasks);

            if (await db.Restores.Where(x => x.Id == restore.Id).Select(x => x.Status).FirstAsync(cancellationToken) == RestoreRunStatus.Canceled)
            {
                _logger.Information("Restore {RestoreId} observed cancellation and will not overwrite canceled status.", restore.Id);
                return;
            }

            var statuses = await db.RestoreTables.Where(x => x.RestoreId == restore.Id).Select(x => x.Status).ToListAsync(cancellationToken);
            var tableCount = await db.RestoreTables.CountAsync(x => x.RestoreId == restore.Id, cancellationToken);
            restore.Status = AggregateRestoreStatus(statuses);
            restore.CompletedAt = DateTimeOffset.UtcNow;
            restore.FailureReason = restore.Status is RestoreRunStatus.Failed or RestoreRunStatus.PartiallySucceeded
                ? await GetRestoreFailureReasonAsync(restore.Id, cancellationToken)
                : null;
            restore.Error = restore.FailureReason;
            await db.SaveChangesAsync(cancellationToken);
            _logger.Information("Restore {RestoreId} finished with status {Status}. Failure reason: {FailureReason}.", restore.Id, restore.Status, restore.FailureReason);
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
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("failed", AuditEntityType.Restore, restore.Id.ToString(), new { error = ex.Message, restore.FailureReason });
        }
    }

    private async Task RunTableAsync(Guid restoreId, Guid tableId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var scopedClickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        var scopedAudit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var scopedOptions = scope.ServiceProvider.GetRequiredService<IOptions<ChoboBackupRestoreOptions>>();
        var scopedTestHooks = scope.ServiceProvider.GetRequiredService<ITestHookCoordinator>();

        var restore = await scopedDb.Restores
            .Include(x => x.TargetCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Backup).ThenInclude(x => x!.Target)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstAsync(x => x.Id == restoreId, cancellationToken);
        var table = restore.Tables.Single(x => x.Id == tableId);
        if (restore.Status == RestoreRunStatus.Canceled)
        {
            _logger.Information("Restore table {RestoreTableId} skipped because restore {RestoreId} was canceled.", table.Id, restore.Id);
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
            var orderedShards = table.Shards
                .OrderBy(x => x.TargetShardNumber ?? int.MaxValue)
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

            foreach (var shard in orderedShards.Where(x => x.Status is RestoreTableStatus.Queued or RestoreTableStatus.Running))
            {
                if (await scopedDb.Restores.Where(x => x.Id == restore.Id).Select(x => x.Status).FirstAsync(cancellationToken) == RestoreRunStatus.Canceled)
                {
                    _logger.Information("Restore table {RestoreTableId} stopped before shard {ShardId} because restore {RestoreId} was canceled.", table.Id, shard.Id, restore.Id);
                    return;
                }
                var endpoint = new ClickHouseNodeEndpoint(shard.TargetHost, shard.TargetPort, shard.TargetUseTls);
                var backupShard = backupTable.Shards.Single(x => x.Id == shard.BackupTableShardId);
                await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, $"CREATE DATABASE IF NOT EXISTS {ClickHouseSql.Identifier(shard.RestoreDatabase)}", cancellationToken);
                if (usesTemporaryShardTables)
                {
                    await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, ClickHouseSql.RewriteCreateTableNameIfNotExists(backupTable.SchemaDefinition!.CreateTableSql, table.TargetDatabase, table.TargetTable), cancellationToken);
                }

                shard.Status = RestoreTableStatus.Running;
                shard.StartedAt ??= DateTimeOffset.UtcNow;
                await scopedDb.SaveChangesAsync(cancellationToken);
                await scopedAudit.RecordAsync("shard-started", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber, targetNode = $"{shard.TargetHost}:{shard.TargetPort}", layout = restore.Layout });

                try
                {
                    if (!string.IsNullOrWhiteSpace(shard.ClickHouseOperationId))
                    {
                        var status = await scopedClickHouse.GetOperationStatusAsync(endpoint, restore.TargetCluster!, shard.ClickHouseOperationId, cancellationToken);
                        if (status.Exists)
                        {
                            await PollRestoreShardAsync(scopedClickHouse, endpoint, restore.TargetCluster!, shard, status, scopedOptions.Value.PollInterval, cancellationToken);
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
                        await PollRestoreShardAsync(scopedClickHouse, endpoint, restore.TargetCluster!, shard, new ClickHouseOperationStatus(true, operation.Status, null), scopedOptions.Value.PollInterval, cancellationToken);
                    }

                    if (!string.Equals(shard.RestoreTableName, table.TargetTable, StringComparison.Ordinal))
                    {
                        var insertSql = table.Warning is null
                            ? $"INSERT INTO {ClickHouseSql.Qualified(table.TargetDatabase, table.TargetTable)} SELECT * FROM {ClickHouseSql.Qualified(shard.RestoreDatabase, shard.RestoreTableName)}"
                            : BuildMismatchAppendInsertSql(existing!, backupTable.SchemaDefinition!, table.TargetDatabase, table.TargetTable, shard.RestoreTableName);
                        await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, insertSql, cancellationToken);
                        await scopedClickHouse.ExecuteAsync(endpoint, restore.TargetCluster!, $"DROP TABLE IF EXISTS {ClickHouseSql.Qualified(shard.RestoreDatabase, shard.RestoreTableName)}", cancellationToken);
                    }

                    shard.Status = RestoreTableStatus.Succeeded;
                    shard.CompletedAt = DateTimeOffset.UtcNow;
                    await scopedDb.SaveChangesAsync(cancellationToken);
                    await scopedAudit.RecordAsync("shard-succeeded", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber, shard.ClickHouseStatus });
                }
                catch (Exception ex)
                {
                    shard.Status = RestoreTableStatus.Failed;
                    shard.Error = ex.Message;
                    shard.CompletedAt = DateTimeOffset.UtcNow;
                    await scopedDb.SaveChangesAsync(CancellationToken.None);
                    await scopedAudit.RecordAsync("shard-failed", AuditEntityType.RestoreTableShard, shard.Id.ToString(), new { error = ex.Message, sourceShard = shard.SourceShardNumber, targetShard = shard.TargetShardNumber });
                }
            }

            table.Status = AggregateRestoreTableStatus(table.Shards.Select(x => x.Status).ToList());
            table.ClickHouseStatus = table.Shards.Any(x => x.Status == RestoreTableStatus.Failed)
                ? "PARTIAL_OR_FAILED"
                : table.Shards.LastOrDefault(x => x.Status == RestoreTableStatus.Succeeded)?.ClickHouseStatus ?? table.ClickHouseStatus;
            table.Error = table.Status is RestoreTableStatus.Failed or RestoreTableStatus.PartiallySucceeded
                ? BuildRestoreShardFailureReason(table.Shards.Where(x => x.Status == RestoreTableStatus.Failed).ToList())
                : null;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await scopedDb.SaveChangesAsync(cancellationToken);
            _logger.Information("Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} completed with ClickHouse status {Status}.", table.Id, table.TargetDatabase, table.TargetTable, table.ClickHouseStatus);
            var tableAction = table.Status == RestoreTableStatus.Succeeded ? "table-succeeded" : table.Status == RestoreTableStatus.PartiallySucceeded ? "table-partially-succeeded" : "table-failed";
            await scopedAudit.RecordAsync(tableAction, AuditEntityType.RestoreTable, table.Id.ToString(), new { table.TargetDatabase, table.TargetTable, shardCount = table.Shards.Count, failureReason = table.Error });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Restore table {RestoreTableId} {TargetDatabase}.{TargetTable} failed.", table.Id, table.TargetDatabase, table.TargetTable);
            table.Status = RestoreTableStatus.Failed;
            table.Error = ex.Message;
            table.CompletedAt = DateTimeOffset.UtcNow;
            await FailPendingRestoreShardsAsync(table, ex.Message, scopedAudit);
            await scopedDb.SaveChangesAsync(CancellationToken.None);
            await scopedAudit.RecordAsync("table-failed", AuditEntityType.RestoreTable, table.Id.ToString(), new { error = ex.Message });
        }
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

    private static async Task PollRestoreShardAsync(IClickHouseAdapter adapter, ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, RestoreTableShardEntity shard, ClickHouseOperationStatus current, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!current.Exists)
            {
                throw MissingOperationException(shard.ClickHouseOperationId);
            }

            shard.ClickHouseStatus = current.Status;
            if (string.Equals(current.Status, "RESTORED", StringComparison.OrdinalIgnoreCase))
            {
                return;
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

    private int EffectiveMaxDop(ClickHouseClusterEntity cluster) =>
        Math.Max(1, cluster.BackupRestoreMaxDop is > 0 ? cluster.BackupRestoreMaxDop.Value : options.Value.MaxDop <= 0 ? 3 : options.Value.MaxDop);

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


