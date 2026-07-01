using System.Text.Json;
using Chobo.Contracts;
using ChoboServer;
using ChoboServer.Application;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace ChoboServer.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route(ChoboApi.ApiPrefix + "/test-hooks")]
public sealed class TestHooksController(
    ChoboDbContext db,
    IBackupRestoreQueues queues,
    IOptions<ChoboTestHooksOptions> options,
    IOptions<ChoboStorageOptions> storageOptions,
    ITestHookCoordinator testHooks,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost("seed-missing-backup-operation")]
    public async Task<ActionResult<BackupDto>> SeedMissingBackupOperation(SeedMissingBackupOperationRequest request, CancellationToken cancellationToken)
    {
        if (!TestHooksAvailable()) return NotFound();
        if (request.SourceClusterId == Guid.Empty || request.TargetId == Guid.Empty)
        {
            return BadRequest(new ErrorResponse("Source cluster id and target id are required."));
        }
        if (string.IsNullOrWhiteSpace(request.Database) || string.IsNullOrWhiteSpace(request.Table))
        {
            return BadRequest(new ErrorResponse("Database and table are required."));
        }

        var cluster = await db.ClickHouseClusters.Include(x => x.AccessNodes).FirstOrDefaultAsync(x => x.Id == request.SourceClusterId, cancellationToken);
        if (cluster is null || cluster.AccessNodes.Count == 0)
        {
            return BadRequest(new ErrorResponse("Source cluster was not found or has no access nodes."));
        }
        if (!await db.BackupTargets.AnyAsync(x => x.Id == request.TargetId, cancellationToken))
        {
            return BadRequest(new ErrorResponse("Backup target was not found."));
        }

        var schema = new SchemaDefinitionEntity
        {
            SchemaHash = $"test-hook-{Guid.NewGuid():N}",
            Database = request.Database,
            Table = request.Table,
            Engine = "MergeTree",
            CreateTableSql = $"CREATE TABLE {ClickHouseSql.Qualified(request.Database, request.Table)} (id UInt64) ENGINE = MergeTree ORDER BY id",
            ColumnsJson = """[{"name":"id","type":"UInt64","defaultKind":"","defaultExpression":""}]"""
        };
        var backup = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Running,
            SourceClusterId = request.SourceClusterId,
            TargetId = request.TargetId,
            RequestedByName = "system",
            StartedAt = DateTimeOffset.UtcNow
        };
        var table = new BackupTableEntity
        {
            Database = request.Database,
            Table = request.Table,
            Engine = "MergeTree",
            DataBackedUp = true,
            SchemaDefinition = schema,
            StoragePath = $"backups/{Uri.EscapeDataString(request.Database)}/{Uri.EscapeDataString(request.Table)}/test-hook/{backup.Id:N}",
            Status = BackupTableStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };

        var node = cluster.AccessNodes[0];
        var shardCount = Math.Max(1, request.ShardCount);
        for (var shardNumber = 1; shardNumber <= shardCount; shardNumber++)
        {
            table.Shards.Add(new BackupTableShardEntity
            {
                SourceShardNumber = shardNumber,
                SourceShardName = shardCount == 1 ? "single" : $"shard-{shardNumber}",
                ReplicaNumber = 1,
                Host = node.Host,
                Port = node.Port,
                UseTls = node.UseTls,
                StoragePath = $"{table.StoragePath}/shards/shard-{shardNumber:0000}",
                Status = BackupTableStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                ClickHouseOperationId = $"missing-from-system-backups-{Guid.NewGuid():N}"
            });
        }

        backup.Tables.Add(table);
        db.Backups.Add(backup);
        await db.SaveChangesAsync(cancellationToken);
        await queues.QueueBackupAsync(backup.Id, backup.ContentMode, cancellationToken);

        var loaded = await db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).FirstAsync(x => x.Id == backup.Id, cancellationToken);
        return BackupRestoreMapping.ToDto(loaded);
    }

    [HttpPost("delay-next-backup-before-poll")]
    public IActionResult DelayNextBackupBeforePoll()
    {
        if (!TestHooksAvailable()) return NotFound();
        testHooks.DelayNextBackupBeforePoll();
        return Ok(new { delayed = "backup-before-poll" });
    }

    [HttpPost("delay-next-restore-before-poll")]
    public IActionResult DelayNextRestoreBeforePoll()
    {
        if (!TestHooksAvailable()) return NotFound();
        testHooks.DelayNextRestoreBeforePoll();
        return Ok(new { delayed = "restore-before-poll" });
    }

    [HttpPost("crash")]
    public IActionResult Crash()
    {
        if (!TestHooksAvailable()) return NotFound();
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Environment.Exit(137);
        });
        return Ok(new { crashing = true });
    }

    [HttpPost("set-future-schema-version-and-crash")]
    public async Task<IActionResult> SetFutureSchemaVersionAndCrash(CancellationToken cancellationToken)
    {
        if (!TestHooksAvailable()) return NotFound();
        var schema = await db.SchemaStates.SingleAsync(cancellationToken);
        schema.SchemaVersion = ChoboApi.SchemaVersion + 1;
        schema.ProductVersion = "future-test";
        await db.SaveChangesAsync(cancellationToken);
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Environment.Exit(137);
        });
        return Ok(new { schemaVersion = schema.SchemaVersion, crashing = true });
    }

    [HttpPost("seed-export-import-graph")]
    public async Task<IActionResult> SeedExportImportGraph(CancellationToken cancellationToken)
    {
        if (!TestHooksAvailable()) return NotFound();
        var user = await db.Users.SingleAsync(x => x.UserName == "admin", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var clusterId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var schemaDefinitionId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var backupTableId = Guid.NewGuid();
        var backupTableShardId = Guid.NewGuid();
        var restoreId = Guid.NewGuid();
        var restoreTableId = Guid.NewGuid();
        var restoreTableShardId = Guid.NewGuid();

        db.ClickHouseClusters.Add(new ClickHouseClusterEntity
        {
            Id = clusterId,
            Name = "system-export-cluster",
            Mode = ClusterMode.Cluster,
            ClickHouseClusterName = "system_export_cluster_name",
            BackupRestoreMaxDop = 2,
            EncryptedUserName = "encrypted-user-from-source",
            EncryptedUserNameKeyId = Guid.NewGuid(),
            EncryptedPassword = "encrypted-password-from-source",
            EncryptedPasswordKeyId = Guid.NewGuid(),
            CreatedAt = now,
            AccessNodes =
            [
                new ClickHouseAccessNodeEntity { Id = Guid.NewGuid(), Host = "source-cluster", Port = 9000, UseTls = false }
            ]
        });
        db.BackupTargets.Add(new BackupTargetEntity
        {
            Id = targetId,
            Name = "system-export-target",
            Type = StorageProviderTypes.S3,
            SettingsJson = JsonSerializer.Serialize(new S3TargetSettingsDto("http://backup-s3:9000", "us-east-1", "backups", "system-export", true)),
            SecretsJson = "{}",
            CreatedAt = now
        });
        db.BackupPolicies.Add(new BackupPolicyEntity
        {
            Id = policyId,
            Name = "system-export-policy",
            SourceClusterId = clusterId,
            TargetId = targetId,
            SelectorJsonVersion = 1,
            SelectorJson = JsonSerializer.Serialize(PolicySelector.Empty),
            FullRetentionMinutes = 60,
            IncrementalRetentionMinutes = 30,
            MinBackupsToKeep = 1,
            MinFullBackupsToKeep = 1,
            CreatedAt = now
        });
        db.BackupSchedules.Add(new BackupScheduleEntity
        {
            Id = scheduleId,
            Name = "system-export-schedule",
            PolicyId = policyId,
            BackupType = BackupType.Full,
            CronExpression = "0 0 2 * * ?",
            TimeZoneId = "UTC",
            IsEnabled = true,
            CreatedAt = now
        });
        db.SchemaDefinitions.Add(new SchemaDefinitionEntity
        {
            Id = schemaDefinitionId,
            SchemaHash = "system-export-schema",
            Database = "system_export_db",
            Table = "system_export_table",
            Engine = "MergeTree",
            CreateTableSql = "CREATE TABLE system_export_db.system_export_table (id UInt64) ENGINE = MergeTree ORDER BY id",
            ColumnsJson = "[{\"name\":\"id\",\"type\":\"UInt64\"}]",
            CreatedAt = now
        });
        db.Backups.Add(new BackupEntity
        {
            Id = backupId,
            TriggerType = BackupTriggerType.Scheduled,
            Status = BackupRunStatus.Succeeded,
            BackupType = BackupType.Full,
            SourceClusterId = clusterId,
            TargetId = targetId,
            PolicyId = policyId,
            ScheduleId = scheduleId,
            RequestedByUserId = user.Id,
            RequestedByName = user.UserName,
            CreatedAt = now,
            QueuedAt = now,
            StartedAt = now.AddSeconds(1),
            CompletedAt = now.AddSeconds(2)
        });
        db.BackupTables.Add(new BackupTableEntity
        {
            Id = backupTableId,
            BackupId = backupId,
            EffectiveBackupType = BackupType.Full,
            Database = "system_export_db",
            Table = "system_export_table",
            Engine = "MergeTree",
            DataBackedUp = true,
            SchemaDefinitionId = schemaDefinitionId,
            StoragePath = "s3://backups/system-export/system_export_table",
            Status = BackupTableStatus.Succeeded,
            StartedAt = now.AddSeconds(1),
            CompletedAt = now.AddSeconds(2)
        });
        db.BackupTableShards.Add(new BackupTableShardEntity
        {
            Id = backupTableShardId,
            BackupTableId = backupTableId,
            EffectiveBackupType = BackupType.Full,
            SourceShardNumber = 1,
            SourceShardName = "shard1",
            ReplicaNumber = 1,
            Host = "source-cluster",
            Port = 9000,
            UseTls = false,
            StoragePath = "s3://backups/system-export/system_export_table/shard1",
            Status = BackupTableStatus.Succeeded,
            StartedAt = now.AddSeconds(1),
            CompletedAt = now.AddSeconds(2)
        });
        db.Restores.Add(new RestoreEntity
        {
            Id = restoreId,
            BackupId = backupId,
            TargetClusterId = clusterId,
            Status = RestoreRunStatus.Succeeded,
            Append = false,
            AllowSchemaMismatch = false,
            Layout = RestoreLayout.Preserve,
            RequestJson = "{\"mode\":\"system-test\"}",
            RequestedByUserId = user.Id,
            RequestedByName = user.UserName,
            CreatedAt = now,
            QueuedAt = now,
            StartedAt = now.AddSeconds(3),
            CompletedAt = now.AddSeconds(4)
        });
        db.RestoreTables.Add(new RestoreTableEntity
        {
            Id = restoreTableId,
            RestoreId = restoreId,
            BackupTableId = backupTableId,
            SourceDatabase = "system_export_db",
            SourceTable = "system_export_table",
            TargetDatabase = "system_export_db_restored",
            TargetTable = "system_export_table_restored",
            Append = false,
            AllowSchemaMismatch = false,
            SchemaOnly = false,
            Status = RestoreTableStatus.Succeeded,
            StartedAt = now.AddSeconds(3),
            CompletedAt = now.AddSeconds(4)
        });
        db.RestoreTableShards.Add(new RestoreTableShardEntity
        {
            Id = restoreTableShardId,
            RestoreTableId = restoreTableId,
            BackupTableShardId = backupTableShardId,
            SourceShardNumber = 1,
            TargetShardNumber = 1,
            TargetShardName = "shard1",
            TargetReplicaNumber = 1,
            TargetHost = "source-cluster",
            TargetPort = 9000,
            TargetUseTls = false,
            LayoutRole = "primary",
            RestoreDatabase = "system_export_db_restored",
            RestoreTableName = "system_export_table_restored",
            Status = RestoreTableStatus.Succeeded,
            StartedAt = now.AddSeconds(3),
            CompletedAt = now.AddSeconds(4)
        });
        db.AuditEntries.Add(new AuditEntryEntity { Timestamp = now, ActorName = "system", Action = "source-export-audit-marker", EntityType = "test", EntityId = backupId.ToString(), Details = "{}" });
        db.ApplicationLogEntries.Add(new ApplicationLogEntryEntity { Timestamp = now, Level = "Information", RenderedMessage = "source export log marker", Properties = "{}" });

        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { clusterId, targetId, policyId, scheduleId, schemaDefinitionId, backupId, backupTableId, backupTableShardId, restoreId, restoreTableId, restoreTableShardId });
    }

    [HttpPost("seed-dashboard-failed-backup")]
    public async Task<IActionResult> SeedDashboardFailedBackup(CancellationToken cancellationToken)
    {
        if (!TestHooksAvailable()) return NotFound();
        var user = await db.Users.SingleAsync(x => x.UserName == "admin", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var clusterId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var schemaDefinitionId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var backupTableId = Guid.NewGuid();
        var backupTableShardId = Guid.NewGuid();
        var failureReason = string.Join("\n",
            "Chobo.UiTests.IntentionalDashboardFailureException: first line for dashboard failure preview",
            "   at Chobo.UiTests.BackupFailureProbe.Run()",
            "   at Chobo.UiTests.BackupFailureProbe.VerifyExpandableError()",
            "InnerException: this extra diagnostic line should only appear after expanding the failure details.",
            "Stack frame 42: generated by the dashboard failure UI test hook.");

        db.ClickHouseClusters.Add(new ClickHouseClusterEntity
        {
            Id = clusterId,
            Name = "ui-dashboard-failure-source",
            Mode = ClusterMode.SingleInstance,
            BackupRestoreMaxDop = 1,
            CreatedAt = now,
            AccessNodes =
            [
                new ClickHouseAccessNodeEntity { Id = Guid.NewGuid(), Host = "missing-clickhouse", Port = 9000, UseTls = false }
            ]
        });
        db.BackupTargets.Add(new BackupTargetEntity
        {
            Id = targetId,
            Name = "ui-dashboard-failure-target",
            Type = StorageProviderTypes.S3,
            SettingsJson = JsonSerializer.Serialize(new S3TargetSettingsDto("http://backup-s3:9000", "us-east-1", "data-bucket", "ui-dashboard-failure", true)),
            SecretsJson = "{}",
            CreatedAt = now
        });
        db.BackupPolicies.Add(new BackupPolicyEntity
        {
            Id = policyId,
            Name = "ui-dashboard-failure-policy",
            SourceClusterId = clusterId,
            TargetId = targetId,
            SelectorJsonVersion = 1,
            SelectorJson = JsonSerializer.Serialize(PolicySelector.Empty),
            MinBackupsToKeep = 1,
            MinFullBackupsToKeep = 1,
            CreatedAt = now
        });
        db.BackupSchedules.Add(new BackupScheduleEntity
        {
            Id = scheduleId,
            Name = "ui-dashboard-failure-schedule",
            PolicyId = policyId,
            BackupType = BackupType.Full,
            CronExpression = "0 0 2 * * ?",
            TimeZoneId = "UTC",
            IsEnabled = true,
            CreatedAt = now
        });
        db.SchemaDefinitions.Add(new SchemaDefinitionEntity
        {
            Id = schemaDefinitionId,
            SchemaHash = $"dashboard-failure-{Guid.NewGuid():N}",
            Database = "ui_dashboard_failure",
            Table = "orders",
            Engine = "MergeTree",
            CreateTableSql = "CREATE TABLE ui_dashboard_failure.orders (id UInt64) ENGINE = MergeTree ORDER BY id",
            ColumnsJson = """[{"name":"id","type":"UInt64"}]""",
            CreatedAt = now
        });
        db.Backups.Add(new BackupEntity
        {
            Id = backupId,
            TriggerType = BackupTriggerType.Scheduled,
            Status = BackupRunStatus.Failed,
            BackupType = BackupType.Full,
            ContentMode = BackupContentMode.SchemaAndData,
            SourceClusterId = clusterId,
            TargetId = targetId,
            PolicyId = policyId,
            ScheduleId = scheduleId,
            RequestedByUserId = user.Id,
            RequestedByName = user.UserName,
            CreatedAt = now,
            QueuedAt = now,
            StartedAt = now.AddSeconds(1),
            CompletedAt = now.AddSeconds(2),
            Error = failureReason,
            FailureReason = failureReason
        });
        db.BackupTables.Add(new BackupTableEntity
        {
            Id = backupTableId,
            BackupId = backupId,
            EffectiveBackupType = BackupType.Full,
            Database = "ui_dashboard_failure",
            Table = "orders",
            Engine = "MergeTree",
            DataBackedUp = true,
            SchemaDefinitionId = schemaDefinitionId,
            StoragePath = "s3://data-bucket/ui-dashboard-failure/orders",
            Status = BackupTableStatus.Failed,
            StartedAt = now.AddSeconds(1),
            CompletedAt = now.AddSeconds(2),
            Error = failureReason
        });
        db.BackupTableShards.Add(new BackupTableShardEntity
        {
            Id = backupTableShardId,
            BackupTableId = backupTableId,
            EffectiveBackupType = BackupType.Full,
            SourceShardNumber = 1,
            SourceShardName = "single",
            ReplicaNumber = 1,
            Host = "missing-clickhouse",
            Port = 9000,
            UseTls = false,
            StoragePath = "s3://data-bucket/ui-dashboard-failure/orders/shard-0001",
            Status = BackupTableStatus.Failed,
            StartedAt = now.AddSeconds(1),
            CompletedAt = now.AddSeconds(2),
            Error = failureReason
        });
        db.AuditEntries.Add(new AuditEntryEntity { Timestamp = now, ActorName = "system", Action = "dashboard-failure-seeded", EntityType = AuditEntityTypes.ToStorageValue(AuditEntityType.Backup), EntityId = backupId.ToString(), Details = JsonSerializer.Serialize(new { operationId = backupId, scheduleId }) });

        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { clusterId, targetId, policyId, scheduleId, backupId, backupTableId, backupTableShardId, failureReason });
    }

    [HttpPost("seed-large-metadata-graph")]
    public async Task<IActionResult> SeedLargeMetadataGraph(SeedLargeMetadataGraphRequest request, CancellationToken cancellationToken)
    {
        if (!TestHooksAvailable()) return NotFound();

        var backupCount = Math.Clamp(request.BackupCount ?? 300, 1, 600);
        var tablesPerBackup = Math.Clamp(request.TablesPerBackup ?? 10, 1, 50);
        var shardsPerTable = Math.Clamp(request.ShardsPerTable ?? 4, 1, 16);
        var restoreCount = Math.Clamp(request.RestoreCount ?? 60, 0, backupCount);
        var completedQueueRows = Math.Clamp(request.CompletedQueueRows ?? 1000, 0, backupCount * tablesPerBackup * shardsPerTable);
        var now = DateTimeOffset.UtcNow;
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.UserName == "admin", cancellationToken);
        var previousAutoDetectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            var clusterId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var policyId = Guid.NewGuid();
            var scheduleId = Guid.NewGuid();
            var sampleBackupId = Guid.Empty;
            var sampleTableName = "";
            var backups = new List<BackupEntity>(backupCount);
            var schemas = new List<SchemaDefinitionEntity>(backupCount * tablesPerBackup);
            var backupTables = new List<BackupTableEntity>(backupCount * tablesPerBackup);
            var backupShards = new List<BackupTableShardEntity>(backupCount * tablesPerBackup * shardsPerTable);
            var restores = new List<RestoreEntity>(restoreCount);
            var restoreTables = new List<RestoreTableEntity>(restoreCount * Math.Min(tablesPerBackup, 5));
            var restoreShards = new List<RestoreTableShardEntity>(restoreCount * Math.Min(tablesPerBackup, 5) * shardsPerTable);
            var queueItems = new List<BackupRestoreQueueItemEntity>(completedQueueRows);
            var backupTablesByBackup = new Dictionary<Guid, List<BackupTableEntity>>(backupCount);
            var backupShardsByTable = new Dictionary<Guid, List<BackupTableShardEntity>>(backupCount * tablesPerBackup);

            db.ClickHouseClusters.Add(new ClickHouseClusterEntity
            {
                Id = clusterId,
                Name = "large-metadata-source",
                Mode = ClusterMode.Cluster,
                ClickHouseClusterName = "large_metadata_cluster",
                BackupRestoreMaxDop = 8,
                CreatedAt = now,
                AccessNodes =
                [
                    new ClickHouseAccessNodeEntity { Id = Guid.NewGuid(), Host = "large-metadata-node-1", Port = 9000, UseTls = false },
                    new ClickHouseAccessNodeEntity { Id = Guid.NewGuid(), Host = "large-metadata-node-2", Port = 9000, UseTls = false }
                ]
            });
            db.BackupTargets.Add(new BackupTargetEntity
            {
                Id = targetId,
                Name = "large-metadata-target",
                Type = StorageProviderTypes.S3,
                SettingsJson = JsonSerializer.Serialize(new S3TargetSettingsDto("http://backup-s3:9000", "us-east-1", "backups", "large-metadata", true)),
                SecretsJson = "{}",
                CreatedAt = now
            });
            db.BackupPolicies.Add(new BackupPolicyEntity
            {
                Id = policyId,
                Name = "large-metadata-policy",
                SourceClusterId = clusterId,
                TargetId = targetId,
                SelectorJsonVersion = 1,
                SelectorJson = JsonSerializer.Serialize(PolicySelector.Empty),
                FullRetentionMinutes = 7 * 24 * 60,
                IncrementalRetentionMinutes = 7 * 24 * 60,
                MinBackupsToKeep = 20,
                MinFullBackupsToKeep = 10,
                CreatedAt = now
            });
            db.BackupSchedules.Add(new BackupScheduleEntity
            {
                Id = scheduleId,
                Name = "large-metadata-schedule",
                PolicyId = policyId,
                BackupType = BackupType.Full,
                CronExpression = "0 0 2 * * ?",
                TimeZoneId = "UTC",
                IsEnabled = true,
                CreatedAt = now
            });

            for (var backupIndex = 0; backupIndex < backupCount; backupIndex++)
            {
                var backupId = Guid.NewGuid();
                if (backupIndex == 0)
                {
                    sampleBackupId = backupId;
                }

                var completedAt = now.AddMinutes(-(backupCount - backupIndex));
                backups.Add(new BackupEntity
                {
                    Id = backupId,
                    TriggerType = BackupTriggerType.Scheduled,
                    Status = BackupRunStatus.Succeeded,
                    BackupType = BackupType.Full,
                    ContentMode = BackupContentMode.SchemaAndData,
                    SourceClusterId = clusterId,
                    TargetId = targetId,
                    PolicyId = policyId,
                    ScheduleId = scheduleId,
                    RequestedByUserId = user.Id,
                    RequestedByName = user.UserName,
                    CreatedAt = completedAt.AddMinutes(-2),
                    QueuedAt = completedAt.AddMinutes(-2),
                    StartedAt = completedAt.AddMinutes(-1),
                    CompletedAt = completedAt
                });
                backupTablesByBackup[backupId] = new List<BackupTableEntity>(tablesPerBackup);

                for (var tableIndex = 0; tableIndex < tablesPerBackup; tableIndex++)
                {
                    var database = $"stress_db_{backupIndex % 12:00}";
                    var tableName = $"table_{backupIndex:0000}_{tableIndex:0000}";
                    sampleTableName = sampleTableName.Length == 0 ? $"{database}.{tableName}" : sampleTableName;
                    var schemaId = Guid.NewGuid();
                    var tableId = Guid.NewGuid();
                    schemas.Add(new SchemaDefinitionEntity
                    {
                        Id = schemaId,
                        SchemaHash = $"large-metadata-{backupIndex:0000}-{tableIndex:0000}-{Guid.NewGuid():N}",
                        Database = database,
                        Table = tableName,
                        Engine = "ReplicatedMergeTree",
                        CreateTableSql = $"CREATE TABLE {ClickHouseSql.Qualified(database, tableName)} (id UInt64, value String) ENGINE = ReplicatedMergeTree('/clickhouse/tables/{{shard}}/{tableName}', '{{replica}}') ORDER BY id",
                        ColumnsJson = """[{""name"":""id"",""type"":""UInt64""},{""name"":""value"",""type"":""String""}]""",
                        CreatedAt = completedAt.AddMinutes(-2)
                    });
                    var backupTable = new BackupTableEntity
                    {
                        Id = tableId,
                        BackupId = backupId,
                        EffectiveBackupType = BackupType.Full,
                        Database = database,
                        Table = tableName,
                        Engine = "ReplicatedMergeTree",
                        DataBackedUp = true,
                        SchemaDefinitionId = schemaId,
                        StoragePath = $"s3://backups/large-metadata/{backupId:N}/{database}/{tableName}",
                        BackupSizeBytes = 8L * 1024 * 1024,
                        Status = BackupTableStatus.Succeeded,
                        StartedAt = completedAt.AddMinutes(-1),
                        CompletedAt = completedAt
                    };
                    backupTables.Add(backupTable);
                    backupTablesByBackup[backupId].Add(backupTable);
                    backupShardsByTable[tableId] = new List<BackupTableShardEntity>(shardsPerTable);

                    for (var shardIndex = 1; shardIndex <= shardsPerTable; shardIndex++)
                    {
                        var shard = new BackupTableShardEntity
                        {
                            Id = Guid.NewGuid(),
                            BackupTableId = tableId,
                            EffectiveBackupType = backupTable.EffectiveBackupType,
                            SourceShardNumber = shardIndex,
                            SourceShardName = $"shard-{shardIndex:0000}",
                            ReplicaNumber = 1,
                            Host = shardIndex % 2 == 0 ? "large-metadata-node-2" : "large-metadata-node-1",
                            Port = 9000,
                            UseTls = false,
                            StoragePath = $"{backupTable.StoragePath}/shards/shard-{shardIndex:0000}",
                            BackupSizeBytes = 2L * 1024 * 1024,
                            Status = BackupTableStatus.Succeeded,
                            StartedAt = completedAt.AddMinutes(-1),
                            CompletedAt = completedAt
                        };
                        backupShards.Add(shard);
                        backupShardsByTable[tableId].Add(shard);
                        if (queueItems.Count < completedQueueRows)
                        {
                            queueItems.Add(new BackupRestoreQueueItemEntity
                            {
                                Kind = BackupRestoreQueueKind.Backup,
                                Position = (queueItems.Count + 1) * 1000L,
                                OperationId = backupId,
                                TableId = tableId,
                                ShardId = shard.Id,
                                ClusterId = clusterId,
                                LogicalShardNumber = shard.SourceShardNumber,
                                LogicalShardName = shard.SourceShardName,
                                NodeHost = shard.Host,
                                NodePort = shard.Port,
                                NodeUseTls = shard.UseTls,
                                CreatedAt = completedAt.AddMinutes(-2),
                                StartedAt = completedAt.AddMinutes(-1),
                                CompletedAt = completedAt
                            });
                        }
                    }
                }
            }

            for (var restoreIndex = 0; restoreIndex < restoreCount; restoreIndex++)
            {
                var sourceBackup = backups[(restoreIndex * Math.Max(1, backupCount / Math.Max(1, restoreCount))) % backups.Count];
                var restoreId = Guid.NewGuid();
                var completedAt = now.AddMinutes(-restoreIndex);
                restores.Add(new RestoreEntity
                {
                    Id = restoreId,
                    BackupId = sourceBackup.Id,
                    TargetClusterId = clusterId,
                    Status = RestoreRunStatus.Succeeded,
                    Append = false,
                    AllowSchemaMismatch = false,
                    Layout = RestoreLayout.Preserve,
                    RequestJson = JsonSerializer.Serialize(new { mode = "large-metadata-stress" }),
                    RequestedByUserId = user.Id,
                    RequestedByName = user.UserName,
                    CreatedAt = completedAt.AddMinutes(-2),
                    QueuedAt = completedAt.AddMinutes(-2),
                    StartedAt = completedAt.AddMinutes(-1),
                    CompletedAt = completedAt
                });

                foreach (var sourceTable in backupTablesByBackup[sourceBackup.Id].Take(Math.Min(tablesPerBackup, 5)))
                {
                    var restoreTableId = Guid.NewGuid();
                    restoreTables.Add(new RestoreTableEntity
                    {
                        Id = restoreTableId,
                        RestoreId = restoreId,
                        BackupTableId = sourceTable.Id,
                        SourceDatabase = sourceTable.Database,
                        SourceTable = sourceTable.Table,
                        TargetDatabase = $"{sourceTable.Database}_restore",
                        TargetTable = sourceTable.Table,
                        Append = false,
                        AllowSchemaMismatch = false,
                        SchemaOnly = false,
                        Status = RestoreTableStatus.Succeeded,
                        StartedAt = completedAt.AddMinutes(-1),
                        CompletedAt = completedAt
                    });
                    foreach (var sourceShard in backupShardsByTable[sourceTable.Id])
                    {
                        restoreShards.Add(new RestoreTableShardEntity
                        {
                            Id = Guid.NewGuid(),
                            RestoreTableId = restoreTableId,
                            BackupTableShardId = sourceShard.Id,
                            SourceShardNumber = sourceShard.SourceShardNumber,
                            TargetShardNumber = sourceShard.SourceShardNumber,
                            TargetShardName = sourceShard.SourceShardName,
                            TargetReplicaNumber = 1,
                            TargetHost = sourceShard.Host,
                            TargetPort = sourceShard.Port,
                            TargetUseTls = sourceShard.UseTls,
                            LayoutRole = "primary",
                            RestoreDatabase = $"{sourceTable.Database}_restore",
                            RestoreTableName = sourceTable.Table,
                            Status = RestoreTableStatus.Succeeded,
                            StartedAt = completedAt.AddMinutes(-1),
                            CompletedAt = completedAt
                        });
                    }
                }
            }

            db.SchemaDefinitions.AddRange(schemas);
            db.Backups.AddRange(backups);
            db.BackupTables.AddRange(backupTables);
            db.BackupTableShards.AddRange(backupShards);
            db.Restores.AddRange(restores);
            db.RestoreTables.AddRange(restoreTables);
            db.RestoreTableShards.AddRange(restoreShards);
            db.BackupRestoreQueueItems.AddRange(queueItems);
            db.AuditEntries.Add(new AuditEntryEntity
            {
                Timestamp = now,
                ActorName = "system",
                Action = "large-metadata-seeded",
                EntityType = "test",
                EntityId = sampleBackupId.ToString(),
                Details = JsonSerializer.Serialize(new { backupCount, tablesPerBackup, shardsPerTable, restoreCount, completedQueueRows, backupTableCount = backupTables.Count, backupShardCount = backupShards.Count, restoreTableCount = restoreTables.Count, restoreShardCount = restoreShards.Count })
            });
            db.ApplicationLogEntries.Add(new ApplicationLogEntryEntity
            {
                Timestamp = now,
                Level = "Information",
                RenderedMessage = $"Seeded large metadata graph with {backupCount} backups, {backupTables.Count} backup tables, {backupShards.Count} backup shards, {restores.Count} restores.",
                Properties = "{}"
            });

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return Ok(new
            {
                clusterId,
                targetId,
                policyId,
                scheduleId,
                sampleBackupId,
                sampleTableName,
                backupCount,
                backupTableCount = backupTables.Count,
                backupShardCount = backupShards.Count,
                restoreCount = restores.Count,
                restoreTableCount = restoreTables.Count,
                restoreShardCount = restoreShards.Count,
                completedQueueRows = queueItems.Count
            });
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetectChanges;
        }
    }
    [HttpPost("delete-sqlite-and-crash")]
    public IActionResult DeleteSqliteAndCrash()
    {
        if (!TestHooksAvailable()) return NotFound();
        var dataDirectory = ChoboPaths.GetDataDirectory(storageOptions.Value.DataDirectory);
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            foreach (var path in Directory.EnumerateFiles(dataDirectory, "chobo.db*"))
            {
                try
                {
                    System.IO.File.Delete(path);
                }
                catch
                {
                    // Test-only best effort before crashing the process.
                }
            }

            Environment.Exit(137);
        });
        return Ok(new { deletingSqlite = true, crashing = true });
    }
    private bool TestHooksAvailable() =>
        options.Value.Enabled && environment.IsEnvironment("SystemTest");
}

public sealed record SeedLargeMetadataGraphRequest(int? BackupCount, int? TablesPerBackup, int? ShardsPerTable, int? RestoreCount, int? CompletedQueueRows);
