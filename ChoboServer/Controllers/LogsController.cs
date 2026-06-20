using Chobo.Contracts;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
public sealed class LogsController(IApplicationLogStore logs, IAuditService audit) : ControllerBase
{
    [HttpGet(ChoboApi.ApiPrefix + "/logs")]
    public Task<PagedResultDto<ApplicationLogEntryDto>> List([FromQuery] DateTimeOffset? startTime, [FromQuery] DateTimeOffset? endTime, [FromQuery] int? last, [FromQuery] int? offset, [FromQuery] int? limit, [FromQuery] string? operationId) =>
        logs.QueryAsync(startTime, endTime, last, offset, limit, operationId);

    [HttpPost(ChoboApi.ApiPrefix + "/logs/clear")]
    public async Task<IActionResult> Clear(ClearApplicationLogsRequest request)
    {
        var deleted = await logs.DeleteBeforeAsync(request.Before);
        await audit.RecordAsync("clear", AuditEntityType.ApplicationLog, null, new { request.Before, deleted });
        return Ok(new { deleted });
    }
}

