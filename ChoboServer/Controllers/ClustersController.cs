using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/clusters")]
public sealed class ClustersController(ClusterApplicationService clusters) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ClusterDto>> List() => clusters.ListAsync();

    [HttpPost]
    public async Task<ActionResult<ClusterDto>> Add(UpsertClusterRequest request)
    {
        try { return await clusters.AddAsync(request); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ClusterDto>> Update(Guid id, UpsertClusterRequest request)
    {
        try
        {
            var result = await clusters.UpdateAsync(id, request);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        return await clusters.RemoveAsync(id) ? NoContent() : NotFound();
    }
}
