using Chobo.Contracts;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
public sealed class AuditController(AuditStore audits, AuditService audit) : ControllerBase
{
    [HttpGet(ChoboApi.ApiPrefix + "/audit")]
    public Task<IReadOnlyList<AuditEntryDto>> List([FromQuery] DateTimeOffset? startTime, [FromQuery] DateTimeOffset? endTime, [FromQuery] int? last) =>
        audits.QueryAsync(startTime, endTime, last);

    [HttpPost(ChoboApi.ApiPrefix + "/audit/clear")]
    public async Task<IActionResult> Clear(ClearAuditEntriesRequest request)
    {
        var deleted = await audits.DeleteBeforeAsync(request.Before);
        await audit.RecordAsync("clear", AuditEntityType.Audit, null, new { request.Before, deleted });
        return Ok(new { deleted });
    }
}
