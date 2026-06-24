using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/queue")]
public sealed class BackupRestoreQueueController(BackupRestoreQueueApplicationService queue) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BackupRestoreQueueItemDto>> List([FromQuery] BackupRestoreQueueKind kind = BackupRestoreQueueKind.All, [FromQuery] string status = "active", [FromQuery] int limit = 500, CancellationToken cancellationToken = default) =>
        queue.ListAsync(kind, status, limit, cancellationToken);

    [HttpPost("items/{id:guid}/move")]
    public async Task<ActionResult<BackupRestoreQueueItemDto>> MoveItem(Guid id, MoveQueueItemRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await queue.MoveItemAsync(id, request, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("tables/{kind}/{tableId:guid}/move")]
    public async Task<ActionResult<IReadOnlyList<BackupRestoreQueueItemDto>>> MoveTable(BackupRestoreQueueKind kind, Guid tableId, MoveQueueItemRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await queue.MoveTableAsync(kind, tableId, request, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("items/{id:guid}/force")]
    public async Task<ActionResult<BackupRestoreQueueItemDto>> Force(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await queue.ForceAsync(id, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }
}