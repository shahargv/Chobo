using Chobo.Contracts;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
public sealed class LogsController(IApplicationLogStore logs, IAuditService audit) : ControllerBase
{
    [HttpGet(ChoboApi.ApiPrefix + "/logs")]
    public Task<IReadOnlyList<ApplicationLogEntryDto>> List([FromQuery] DateTimeOffset? startTime, [FromQuery] DateTimeOffset? endTime, [FromQuery] int? last) =>
        logs.QueryAsync(startTime, endTime, last);

    [HttpPost(ChoboApi.ApiPrefix + "/logs/clear")]
    public async Task<IActionResult> Clear(ClearApplicationLogsRequest request)
    {
        var deleted = await logs.DeleteBeforeAsync(request.Before);
        await audit.RecordAsync("clear", AuditEntityType.ApplicationLog, null, new { request.Before, deleted });
        return Ok(new { deleted });
    }
}
