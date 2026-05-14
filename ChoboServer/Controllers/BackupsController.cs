using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix)]
public sealed class BackupsController(BackupApplicationService backups) : ControllerBase
{
    [HttpPost("backups/manual")]
    public async Task<ActionResult<BackupDto>> Manual(ManualBackupRequest request, CancellationToken cancellationToken)
    {
        try { return await backups.ManualAsync(request, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpGet("backups")]
    public Task<IReadOnlyList<BackupDto>> List(
        [FromQuery] Guid? policyId,
        [FromQuery] string? clusterName,
        [FromQuery] string? tableName,
        [FromQuery] BackupRunStatus? status,
        CancellationToken cancellationToken) =>
        backups.ListAsync(policyId, clusterName, tableName, status, cancellationToken);

    [HttpGet("backups/{id:guid}")]
    public async Task<ActionResult<BackupDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await backups.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpPost("backups/{id:guid}/pin")]
    public async Task<ActionResult<BackupDto>> Pin(Guid id, CancellationToken cancellationToken)
    {
        var result = await backups.PinAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpPost("backups/{id:guid}/unpin")]
    public async Task<ActionResult<BackupDto>> Unpin(Guid id, CancellationToken cancellationToken)
    {
        var result = await backups.UnpinAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpDelete("backups/{id:guid}")]
    public async Task<ActionResult<BackupDto>> Delete(Guid id, [FromQuery] bool force, CancellationToken cancellationToken)
    {
        try
        {
            var result = await backups.RequestDeleteAsync(id, force, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }
}
