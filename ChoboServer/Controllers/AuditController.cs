using Chobo.Contracts;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
public sealed class AuditController(IAuditStore audits, IAuditService audit) : ControllerBase
{
    [HttpGet(ChoboApi.ApiPrefix + "/audit")]
    public Task<PagedResultDto<AuditEntryDto>> List([FromQuery] DateTimeOffset? startTime, [FromQuery] DateTimeOffset? endTime, [FromQuery] int? last, [FromQuery] int? offset, [FromQuery] int? limit, [FromQuery] string? operationId) =>
        audits.QueryAsync(startTime, endTime, last, offset, limit, operationId);

    [HttpPost(ChoboApi.ApiPrefix + "/audit/clear")]
    public async Task<IActionResult> Clear(ClearAuditEntriesRequest request)
    {
        var deleted = await audits.DeleteBeforeAsync(request.Before);
        await audit.RecordAsync("clear", AuditEntityType.Audit, null, new { request.Before, deleted });
        return Ok(new { deleted });
    }
}

