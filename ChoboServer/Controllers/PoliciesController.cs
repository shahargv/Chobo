using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/policies")]
public sealed class PoliciesController(PolicyApplicationService policies) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BackupPolicyDto>> List() => policies.ListAsync();

    [HttpPost]
    public async Task<ActionResult<BackupPolicyDto>> Add(UpsertPolicyRequest request)
    {
        try { return await policies.AddAsync(request); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BackupPolicyDto>> Update(Guid id, UpsertPolicyRequest request)
    {
        try
        {
            var result = await policies.UpdateAsync(id, request);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("{id:guid}/evaluate")]
    public async Task<ActionResult<PolicyEvaluationDto>> Evaluate(Guid id, PolicyEvaluationRequest request)
    {
        try
        {
            var result = await policies.EvaluateAsync(id, request);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        return await policies.RemoveAsync(id) ? NoContent() : NotFound();
    }
}
