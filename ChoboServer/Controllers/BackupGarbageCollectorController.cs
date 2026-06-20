using Chobo.Contracts;
using ChoboServer.BackgroundServices;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix)]
public sealed class BackupGarbageCollectorController(
    BackupsGarbageCollectorBackgroundService garbageCollector,
    IAuditService audit) : ControllerBase
{
    [HttpGet("backups/garbage-collector/status")]
    public BackupGarbageCollectorStatusDto Status() => garbageCollector.GetStatus();

    [HttpPost("backups/garbage-collector/run")]
    public async Task<ActionResult<BackupGarbageCollectorStatusDto>> Run(CancellationToken cancellationToken)
    {
        await audit.RecordAsync("manual-run-requested", AuditEntityType.BackupGarbageCollector, null, new { reason = "manual" });
        return await garbageCollector.RunOnceAsync("manual", cancellationToken);
    }
}
