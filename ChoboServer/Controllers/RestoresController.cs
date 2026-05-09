using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/restores")]
public sealed class RestoresController(RestoreApplicationService restores) : ControllerBase
{
    [HttpPost("initiate")]
    public async Task<ActionResult<RestoreDto>> Initiate(InitiateRestoreRequest request, CancellationToken cancellationToken)
    {
        try { return await restores.InitiateAsync(request, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpGet]
    public Task<IReadOnlyList<RestoreDto>> List(CancellationToken cancellationToken) =>
        restores.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RestoreDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await restores.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }
}
