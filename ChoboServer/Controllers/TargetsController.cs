using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/targets")]
public sealed class TargetsController(TargetApplicationService targets) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BackupTargetDto>> List() => targets.ListAsync();

    [HttpPost("s3")]
    public async Task<ActionResult<BackupTargetDto>> AddS3(UpsertS3TargetRequest request)
    {
        try { return await targets.AddS3Async(request); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPut("{id:guid}/s3")]
    public async Task<ActionResult<BackupTargetDto>> UpdateS3(Guid id, UpsertS3TargetRequest request)
    {
        try
        {
            var result = await targets.UpdateS3Async(id, request);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
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
