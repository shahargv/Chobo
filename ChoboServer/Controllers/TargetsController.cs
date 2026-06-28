using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/targets")]
public sealed class TargetsController(TargetApplicationService targets) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BackupTargetDto>> List([FromQuery] bool includeDeleted = false) => targets.ListAsync(includeDeleted);

    [HttpPost]
    public async Task<ActionResult<BackupTargetDto>> Add(UpsertBackupTargetRequest request, CancellationToken cancellationToken)
    {
        try { return await targets.AddAsync(request, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
        catch (NotSupportedException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BackupTargetDto>> Update(Guid id, UpsertBackupTargetRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await targets.UpdateAsync(id, request, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
        catch (NotSupportedException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("s3")]
    public async Task<ActionResult<BackupTargetDto>> AddS3(UpsertS3TargetRequest request, CancellationToken cancellationToken)
    {
        try { return await targets.AddS3Async(request, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
        catch (NotSupportedException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPut("{id:guid}/s3")]
    public async Task<ActionResult<BackupTargetDto>> UpdateS3(Guid id, UpsertS3TargetRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await targets.UpdateS3Async(id, request, request.AccessKey is not null || request.SecretKey is not null, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
        catch (NotSupportedException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        return await targets.RemoveAsync(id) ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/test-connection")]
    public async Task<ActionResult<StorageConnectionTestResult>> TestConnection(Guid id, CancellationToken cancellationToken)
    {
        var result = await targets.TestConnectionAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }
}
