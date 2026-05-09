using Chobo.Contracts;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
public sealed class LogsAndAuditController(ApplicationLogTimelineStore logs, AuditTimelineStore audits, AuditService audit) : ControllerBase
{
    [HttpGet(ChoboApi.ApiPrefix + "/logs")]
    public Task<IReadOnlyList<LogEntryDto>> Logs([FromQuery] DateTimeOffset? startTime, [FromQuery] DateTimeOffset? endTime, [FromQuery] int? last) =>
        logs.QueryAsync(startTime, endTime, last);

    [HttpPost(ChoboApi.ApiPrefix + "/logs/clear")]
    public async Task<IActionResult> ClearLogs(ClearBeforeRequest request)
    {
        var deleted = await logs.DeleteBeforeAsync(request.Before);
        await audit.RecordAsync("clear", "application-log", null, new { request.Before, deleted });
        return Ok(new { deleted });
    }

    [HttpGet(ChoboApi.ApiPrefix + "/audit")]
    public Task<IReadOnlyList<AuditEntryDto>> Audit([FromQuery] DateTimeOffset? startTime, [FromQuery] DateTimeOffset? endTime, [FromQuery] int? last) =>
        audits.QueryAsync(startTime, endTime, last);

    [HttpPost(ChoboApi.ApiPrefix + "/audit/clear")]
    public async Task<IActionResult> ClearAudit(ClearBeforeRequest request)
    {
        var deleted = await audits.DeleteBeforeAsync(request.Before);
        await audit.RecordAsync("clear", "audit", null, new { request.Before, deleted });
        return Ok(new { deleted });
    }
}
