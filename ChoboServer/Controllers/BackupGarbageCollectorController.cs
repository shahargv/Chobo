using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.BackgroundServices;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix)]
public sealed class BackupGarbageCollectorController(
    BackupsGarbageCollectorBackgroundService garbageCollector,
    BackupGarbageCollectionEvaluationService evaluator,
    IAuditService audit) : ControllerBase
{
    [HttpGet("backups/garbage-collector/status")]
    public BackupGarbageCollectorStatusDto Status() => garbageCollector.GetStatus();

    [HttpGet("backups/garbage-collector/queue")]
    public async Task<IReadOnlyList<BackupGarbageCollectorQueueItemDto>> Queue(CancellationToken cancellationToken) =>
        await garbageCollector.GetQueueAsync(cancellationToken);

    [HttpGet("backups/{backupId:guid}/garbage-collection-evaluation")]
    public async Task<ActionResult<BackupGarbageCollectionEvaluationDto>> Evaluate(Guid backupId, CancellationToken cancellationToken)
    {
        var evaluation = await evaluator.EvaluateAsync(backupId, cancellationToken);
        return evaluation is null ? NotFound() : evaluation;
    }

    [HttpPost("backups/garbage-collector/run")]
    public async Task<ActionResult<BackupGarbageCollectorStatusDto>> Run(CancellationToken cancellationToken)
    {
        await audit.RecordAsync("manual-run-requested", AuditEntityType.BackupGarbageCollector, null, new { reason = "manual" });
        return await garbageCollector.RunOnceAsync("manual", cancellationToken);
    }

    [HttpPost("backups/garbage-collector/run/{backupId:guid}")]
    public async Task<ActionResult<BackupGarbageCollectorStatusDto>> RunOne(Guid backupId, CancellationToken cancellationToken)
    {
        await audit.RecordAsync("manual-run-one-requested", AuditEntityType.BackupGarbageCollector, backupId.ToString(), new { reason = "manual", backupId });
        return await garbageCollector.RunOneAsync(backupId, "manual", cancellationToken);
    }
}
