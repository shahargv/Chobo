using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/clusters")]
public sealed class ClustersController(ClusterApplicationService clusters) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ClusterDto>> List([FromQuery] bool includeDeleted = false) => clusters.ListAsync(includeDeleted);

    [HttpGet("{id:guid}/clickhouse-cluster-names")]
    public async Task<ActionResult<ClickHouseClusterNamesDto>> ListClickHouseClusterNames(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await clusters.ListClickHouseClusterNamesAsync(id, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }
    [HttpGet("{id:guid}/topology")]
    public async Task<ActionResult<ClickHouseClusterTopologyDto>> GetTopology(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await clusters.GetTopologyAsync(id, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("{id:guid}/metadata/refresh")]
    public async Task<ActionResult<ClickHouseClusterMetadataRefreshDto>> RefreshMetadata(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await clusters.RefreshMetadataAsync(id, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

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

    [HttpPost("{id:guid}/credentials")]
    public async Task<ActionResult<ClusterDto>> UpdateCredentials(Guid id, UpdateClusterCredentialsRequest request)
    {
        var result = await clusters.UpdateCredentialsAsync(id, request);
        return result is null ? NotFound() : result;
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        return await clusters.RemoveAsync(id) ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/test-connection")]
    public async Task<ActionResult<ClusterConnectionTestResult>> TestConnection(Guid id, CancellationToken cancellationToken)
    {
        var result = await clusters.TestConnectionAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }
}

