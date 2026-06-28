using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using ChoboServer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.Application;

public sealed class BackupPreparationService(
    ChoboDbContext db,
    IClickHouseAdapter clickHouse,
    PolicySelectorEvaluationService selectorEvaluation,
    BackupRestoreQueueApplicationService queue,
    IAuditService audit,
    Serilog.ILogger logger,
    IOptionsMonitor<ChoboBackupRestoreOptions> backupRestoreOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupPreparationService>();

    public async Task PrepareQueueItemsAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.SourceCluster).ThenInclude(x => x!.AccessNodes)
            .Include(x => x.Target)
            .Include(x => x.Policy)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .FirstOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (backup is null || IsCancellationTerminalStatus(backup.Status))
        {
            return;
        }

        ValidateBackup(backup);
        if (backup.Tables.Count == 0)
        {
            _logger.Information("Preparing backup tables for queued backup {BackupId}.", backup.Id);
            await PrepareTablesAsync(backup, cancellationToken);
        }

        await queue.EnsureBackupQueueItemsAsync(backup.Id, cancellationToken);
    }

    private static void ValidateBackup(BackupEntity backup)
    {
        if (backup.BackupType == BackupType.Incremental && backup.PolicyId is null)
        {
            throw new InvalidOperationException("Incremental backups require a policy.");
        }
        if (backup.SourceCluster is null)
        {
            throw new InvalidOperationException("Backup source cluster was not found.");
        }
        if (backup.ContentMode == BackupContentMode.SchemaAndData && backup.Target is null)
        {
            throw new InvalidOperationException("Backup target was not found.");
        }
        if (backup.ContentMode == BackupContentMode.SchemaOnly && backup.BackupType == BackupType.Incremental)
        {
            throw new InvalidOperationException("Schema-only backups must be full backups.");
        }
    }

    private static bool IsCancellationTerminalStatus(BackupRunStatus status) =>
        status is BackupRunStatus.Canceled or
            BackupRunStatus.ManualDeleteRequested or
            BackupRunStatus.ManualDeleted or
            BackupRunStatus.FailedBackupDeleteRequested or
            BackupRunStatus.FailedBackupDeletedByGarbageCollector or
            BackupRunStatus.BackupExpiredDeleteStarted or
            BackupRunStatus.BackupExpiredDeleted;

    private async Task PrepareTablesAsync(BackupEntity backup, CancellationToken cancellationToken)
    {
        var manualRequest = backup.Policy is null
            ? JsonSerializer.Deserialize<ManualBackupRequest>(backup.ManualRequestJson ?? "", JsonOptions)
            : null;
        var selector = backup.Policy is not null
            ? JsonSerializer.Deserialize<PolicySelector>(backup.Policy.SelectorJson, JsonOptions) ?? PolicySelector.Empty
            : manualRequest?.Selector ?? PolicySelector.Empty;
        var schemaOnly = backup.ContentMode == BackupContentMode.SchemaOnly || (manualRequest?.SchemaOnly ?? false);
        if (schemaOnly && backup.ContentMode != BackupContentMode.SchemaOnly)
        {
            backup.ContentMode = BackupContentMode.SchemaOnly;
            backup.BackupType = BackupType.Full;
            backup.TargetId = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        var topology = await clickHouse.GetTopologyAsync(backup.SourceCluster!, cancellationToken);
        var representatives = SelectShardRepresentatives(topology);
        var inventory = schemaOnly
            ? await ReadClusterSchemaInventoryAsync(backup, topology, cancellationToken)
            : await clickHouse.GetTablesAsync(backup.SourceCluster!, cancellationToken);
        _logger.Information("Backup {BackupId} inventory contains {InventoryCount} table(s).", backup.Id, inventory.Count);
        var selectableInventory = inventory
            .Where(x => !IsExcludedSystemDatabase(x.Database))
            .ToList();
        var selected = selectorEvaluation.Evaluate(selector, new PolicyInventory(selectableInventory.Select(x => new PolicyInventoryTable(x.Database, x.Table)).ToList()));
        _logger.Information("Backup {BackupId} selector matched {SelectedCount} table(s).", backup.Id, selected.Count);
        var selectedSet = selected.Select(x => ClickHouseBackupIdentity.Table(x.Database, x.Table)).ToHashSet(StringComparer.Ordinal);
        var selectedTables = selectableInventory
            .Where(x => selectedSet.Contains(ClickHouseBackupIdentity.Table(x.Database, x.Table)))
            .ToList();
        var selectedSchemaHashes = selectedTables
            .Select(x => x.SchemaHash)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var schemasByHash = await db.SchemaDefinitions
            .Where(x => selectedSchemaHashes.Contains(x.SchemaHash))
            .ToDictionaryAsync(x => x.SchemaHash, StringComparer.Ordinal, cancellationToken);
        var baseBackupCutoff = backup.BackupType == BackupType.Incremental && backup.Policy is not null
            ? backup.CreatedAt.Subtract(TimeSpan.FromHours(ResolveMaxAgeHoursForBaseBackup(backup.Policy)))
            : (DateTimeOffset?)null;
        var parentTablesByIdentity = backup.BackupType == BackupType.Incremental && backup.PolicyId is not null
            ? await FindParentFullTablesAsync(backup.PolicyId.Value, selectedTables, baseBackupCutoff, cancellationToken)
            : [];
        var parentShardsByIdentity = backup.BackupType == BackupType.Incremental && backup.PolicyId is not null
            ? await FindParentFullShardsAsync(backup.PolicyId.Value, selectedTables, baseBackupCutoff, cancellationToken)
            : [];

        foreach (var table in selectedTables)
        {
            if (!schemasByHash.TryGetValue(table.SchemaHash, out var schema))
            {
                schema = new SchemaDefinitionEntity
                {
                    SchemaHash = table.SchemaHash,
                    Database = table.Database,
                    Table = table.Table,
                    Engine = table.Engine,
                    CreateTableSql = table.CreateTableSql,
                    ColumnsJson = table.ColumnsJson
                };
                schemasByHash[table.SchemaHash] = schema;
                db.SchemaDefinitions.Add(schema);
                _logger.Debug("Prepared new schema definition {SchemaDefinitionId} for {Database}.{Table} hash {SchemaHash}.", schema.Id, table.Database, table.Table, table.SchemaHash);
            }

            var dataBackedUp = !schemaOnly && IsMergeTreeDataEngine(table.Engine);
            var tableIdentity = ClickHouseBackupIdentity.Table(table.Database, table.Table);
            var parentTable = backup.BackupType == BackupType.Incremental && dataBackedUp
                ? parentTablesByIdentity.GetValueOrDefault(tableIdentity)
                : null;
            var tableParentShards = dataBackedUp
                ? parentShardsByIdentity
                    .Where(x => x.Value.BackupTable is not null && ClickHouseBackupIdentity.Table(x.Value.BackupTable.Database, x.Value.BackupTable.Table) == tableIdentity)
                    .Select(x => x.Value)
                    .ToList()
                : [];
            var effectiveTableType = parentTable is null && tableParentShards.Count == 0 ? BackupType.Full : BackupType.Incremental;
            var tableParentBackupId = parentTable?.BackupId ?? tableParentShards.Select(x => x.BackupTable?.BackupId).FirstOrDefault(x => x.HasValue);
            var backupTable = new BackupTableEntity
            {
                BackupId = backup.Id,
                EffectiveBackupType = effectiveTableType,
                ParentFullBackupId = parentTable?.BackupId,
                ParentFullBackupTableId = parentTable?.Id,
                Database = table.Database,
                Table = table.Table,
                Engine = table.Engine,
                DataBackedUp = dataBackedUp,
                SchemaDefinitionId = schema.Id,
                StoragePath = BuildStoragePath(backup, table.Database, table.Table, effectiveTableType, tableParentBackupId)
            };
            if (backup.BackupType == BackupType.Incremental && dataBackedUp && parentTable is null && tableParentShards.Count == 0)
            {
                await audit.RecordAsync("incremental-table-fallback-to-full", AuditEntityType.Backup, backup.Id.ToString(), new { table.Database, table.Table, reason = "missing-parent-full-table" });
            }
            if (dataBackedUp)
            {
                var hasAnyShardParent = tableParentShards.Count > 0;
                foreach (var representative in representatives)
                {
                    parentShardsByIdentity.TryGetValue(ClickHouseBackupIdentity.Shard(table.Database, table.Table, representative.ShardNumber), out var parentShard);
                    var effectiveShardType = parentShard is null ? BackupType.Full : BackupType.Incremental;
                    if (backup.BackupType == BackupType.Incremental && (parentTable is not null || hasAnyShardParent) && parentShard is null)
                    {
                        await audit.RecordAsync("incremental-shard-fallback-to-full", AuditEntityType.Backup, backup.Id.ToString(), new { table.Database, table.Table, sourceShard = representative.ShardNumber, reason = "missing-parent-full-shard" });
                    }
                    var shardParentBackupId = parentShard?.BackupTable?.BackupId;
                    var shardPath = BuildShardStoragePath(
                        BuildStoragePath(backup, table.Database, table.Table, effectiveShardType, shardParentBackupId),
                        representative.ShardNumber);
                    backupTable.Shards.Add(new BackupTableShardEntity
                    {
                        EffectiveBackupType = effectiveShardType,
                        ParentFullBackupId = shardParentBackupId,
                        ParentFullBackupTableShardId = parentShard?.Id,
                        SourceShardNumber = representative.ShardNumber,
                        SourceShardName = representative.ShardName,
                        ReplicaNumber = representative.ReplicaNumber,
                        Host = representative.Host,
                        Port = representative.Port,
                        UseTls = representative.UseTls,
                        StoragePath = shardPath
                    });
                }
            }

            db.BackupTables.Add(backupTable);
            if (!schemaOnly)
            {
                _logger.Information("Prepared backup table {Database}.{Table} engine {Engine} dataBackedUp={DataBackedUp}.", table.Database, table.Table, table.Engine, dataBackedUp);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(backup).Collection(x => x.Tables).LoadAsync(cancellationToken);
        _logger.Information("Backup {BackupId} prepared {TableCount} table row(s).", backup.Id, backup.Tables.Count);
        var shardCount = await db.BackupTableShards.CountAsync(x => x.BackupTable!.BackupId == backup.Id, cancellationToken);
        await audit.RecordAsync("tables-prepared", AuditEntityType.Backup, backup.Id.ToString(), new { tableCount = backup.Tables.Count, shardCount });
        await audit.RecordAsync("shards-prepared", AuditEntityType.Backup, backup.Id.ToString(), new { shardCount });
    }


    private async Task<IReadOnlyList<ClickHouseTableInfo>> ReadClusterSchemaInventoryAsync(BackupEntity backup, IReadOnlyList<ClickHouseShardReplicaInfo> topology, CancellationToken cancellationToken)
    {
        var orderedNodes = topology
            .OrderBy(x => x.ShardNumber)
            .ThenBy(x => x.ReplicaNumber)
            .ThenBy(x => x.Host, StringComparer.Ordinal)
            .ThenBy(x => x.Port)
            .ToList();
        if (orderedNodes.Count == 0)
        {
            return await clickHouse.GetTablesAsync(backup.SourceCluster!, cancellationToken);
        }

        var byName = new Dictionary<string, ClickHouseTableInfo>(StringComparer.Ordinal);
        var duplicateCount = 0;
        foreach (var node in orderedNodes)
        {
            var tables = await clickHouse.GetTablesAsync(node.Endpoint, backup.SourceCluster!, cancellationToken);
            foreach (var table in tables)
            {
                var key = ClickHouseBackupIdentity.Table(table.Database, table.Table);
                if (byName.ContainsKey(key))
                {
                    duplicateCount++;
                    continue;
                }

                byName[key] = table;
            }
        }

        if (duplicateCount > 0)
        {
            await audit.RecordAsync("schema-inventory-deduplicated", AuditEntityType.Backup, backup.Id.ToString(), new { operationId = backup.Id, duplicateCount, nodeCount = orderedNodes.Count });
        }

        return byName.Values
            .OrderBy(x => x.Database, StringComparer.Ordinal)
            .ThenBy(x => x.Table, StringComparer.Ordinal)
            .ToList();
    }
    private static bool IsMergeTreeDataEngine(string engine) =>
        engine.EndsWith("MergeTree", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedSystemDatabase(string database) =>
        string.Equals(database, "system", StringComparison.Ordinal) ||
        string.Equals(database, "information_schema", StringComparison.Ordinal) ||
        string.Equals(database, "INFORMATION_SCHEMA", StringComparison.Ordinal);

    private async Task<Dictionary<string, BackupTableEntity>> FindParentFullTablesAsync(Guid policyId, IReadOnlyList<ClickHouseTableInfo> selectedTables, DateTimeOffset? baseBackupCutoff, CancellationToken cancellationToken)
    {
        var selectedIdentities = selectedTables
            .Select(x => ClickHouseBackupIdentity.Table(x.Database, x.Table))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIdentities.Count == 0)
        {
            return [];
        }

        var databases = selectedTables.Select(x => x.Database).Distinct(StringComparer.Ordinal).ToList();
        var tables = selectedTables.Select(x => x.Table).Distinct(StringComparer.Ordinal).ToList();
        var candidates = await db.BackupTables
            .AsNoTracking()
            .Include(x => x.Backup)
            .Include(x => x.Shards)
            .AsSplitQuery()
            .Where(x => x.Backup != null &&
                        x.Backup.PolicyId == policyId &&
                        (x.Backup.Status == BackupRunStatus.Succeeded ||
                         x.Backup.Status == BackupRunStatus.PartiallySucceeded) &&
                        x.Status == BackupTableStatus.Succeeded &&
                        x.EffectiveBackupType == BackupType.Full &&
                        databases.Contains(x.Database) &&
                        tables.Contains(x.Table) &&
                        (baseBackupCutoff == null || (x.Backup.CompletedAt ?? x.Backup.CreatedAt) >= baseBackupCutoff))
            .OrderByDescending(x => x.Backup!.CompletedAt ?? x.Backup.CreatedAt)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(x => selectedIdentities.Contains(ClickHouseBackupIdentity.Table(x.Database, x.Table)))
            .GroupBy(x => ClickHouseBackupIdentity.Table(x.Database, x.Table), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, BackupTableShardEntity>> FindParentFullShardsAsync(Guid policyId, IReadOnlyList<ClickHouseTableInfo> selectedTables, DateTimeOffset? baseBackupCutoff, CancellationToken cancellationToken)
    {
        var selectedIdentities = selectedTables
            .Select(x => ClickHouseBackupIdentity.Table(x.Database, x.Table))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIdentities.Count == 0)
        {
            return [];
        }

        var databases = selectedTables.Select(x => x.Database).Distinct(StringComparer.Ordinal).ToList();
        var tables = selectedTables.Select(x => x.Table).Distinct(StringComparer.Ordinal).ToList();
        var candidates = await db.BackupTableShards
            .AsNoTracking()
            .Include(x => x.BackupTable).ThenInclude(x => x!.Backup)
            .AsSplitQuery()
            .Where(x => x.BackupTable != null &&
                        x.BackupTable.Backup != null &&
                        x.BackupTable.Backup.PolicyId == policyId &&
                        (x.BackupTable.Backup.Status == BackupRunStatus.Succeeded ||
                         x.BackupTable.Backup.Status == BackupRunStatus.PartiallySucceeded) &&
                        x.Status == BackupTableStatus.Succeeded &&
                        x.EffectiveBackupType == BackupType.Full &&
                        databases.Contains(x.BackupTable.Database) &&
                        tables.Contains(x.BackupTable.Table) &&
                        (baseBackupCutoff == null || (x.BackupTable.Backup.CompletedAt ?? x.BackupTable.Backup.CreatedAt) >= baseBackupCutoff))
            .OrderByDescending(x => x.BackupTable!.Backup!.CompletedAt ?? x.BackupTable.Backup.CreatedAt)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(x => x.BackupTable is not null && selectedIdentities.Contains(ClickHouseBackupIdentity.Table(x.BackupTable.Database, x.BackupTable.Table)))
            .GroupBy(x => ClickHouseBackupIdentity.Shard(x.BackupTable!.Database, x.BackupTable.Table, x.SourceShardNumber), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
    }

    private int ResolveMaxAgeHoursForBaseBackup(BackupPolicyEntity policy)
    {
        var maxAgeHours = policy.MaxAgeHoursForBaseBackup ?? backupRestoreOptions.CurrentValue.DefaultMaxAgeHoursForBaseBackup;
        if (maxAgeHours <= 0)
        {
            throw new InvalidOperationException("Max age hours for base backup must be greater than zero.");
        }

        return maxAgeHours;
    }
    private static string BuildStoragePath(BackupEntity backup, string database, string table, BackupType effectiveType, Guid? parentFullBackupId)
    {
        var source = backup.PolicyId is { } policyId ? $"policy-{policyId:N}" : "manual";
        var timestamp = backup.CreatedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        if (effectiveType == BackupType.Incremental)
        {
            var parent = parentFullBackupId is null ? "parent-full-unknown" : $"parent-full-{parentFullBackupId.Value:N}";
            return $"backups/incremental/{source}/{EscapePathPart(database)}/{EscapePathPart(table)}/{parent}/{timestamp}/{backup.Id:N}";
        }

        return $"backups/full/{source}/{EscapePathPart(database)}/{EscapePathPart(table)}/{timestamp}/{backup.Id:N}";
    }

    private static string BuildShardStoragePath(string tablePath, int shardNumber) =>
        $"{tablePath}/shards/shard-{shardNumber:0000}";

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

    private static string EscapePathPart(string value) =>
        Uri.EscapeDataString(value).Replace("%2F", "_", StringComparison.OrdinalIgnoreCase);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
