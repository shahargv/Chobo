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
            S3Path = $"backups/{Uri.EscapeDataString(request.Database)}/{Uri.EscapeDataString(request.Table)}/test-hook/{backup.Id:N}",
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
                S3Path = $"{table.S3Path}/shards/shard-{shardNumber:0000}",
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
            Type = BackupTargetType.S3,
            Endpoint = "http://backup-s3:9000",
            Region = "us-east-1",
            Bucket = "backups",
            PathPrefix = "system-export",
            ForcePathStyle = true,
            EncryptedAccessKey = "encrypted-access-from-source",
            EncryptedAccessKeyKeyId = Guid.NewGuid(),
            EncryptedSecretKey = "encrypted-secret-from-source",
            EncryptedSecretKeyKeyId = Guid.NewGuid(),
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
            S3Path = "s3://backups/system-export/system_export_table",
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
            S3Path = "s3://backups/system-export/system_export_table/shard1",
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

