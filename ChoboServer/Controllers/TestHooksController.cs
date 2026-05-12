using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/test-hooks")]
public sealed class TestHooksController(
    ChoboDbContext db,
    BackupRestoreQueues queues,
    IOptions<ChoboTestHooksOptions> options,
    TestHookCoordinator testHooks) : ControllerBase
{
    [HttpPost("seed-missing-backup-operation")]
    public async Task<ActionResult<BackupDto>> SeedMissingBackupOperation(SeedMissingBackupOperationRequest request, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return NotFound();
        }
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
        await queues.QueueBackupAsync(backup.Id, cancellationToken);

        var loaded = await db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).FirstAsync(x => x.Id == backup.Id, cancellationToken);
        return BackupRestoreMapping.ToDto(loaded);
    }

    [HttpPost("delay-next-backup-before-poll")]
    public IActionResult DelayNextBackupBeforePoll()
    {
        if (!options.Value.Enabled) return NotFound();
        testHooks.DelayNextBackupBeforePoll();
        return Ok(new { delayed = "backup-before-poll" });
    }

    [HttpPost("delay-next-restore-before-poll")]
    public IActionResult DelayNextRestoreBeforePoll()
    {
        if (!options.Value.Enabled) return NotFound();
        testHooks.DelayNextRestoreBeforePoll();
        return Ok(new { delayed = "restore-before-poll" });
    }

    [HttpPost("crash")]
    public IActionResult Crash()
    {
        if (!options.Value.Enabled) return NotFound();
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Environment.Exit(137);
        });
        return Ok(new { crashing = true });
    }
}
