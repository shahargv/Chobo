using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/schedules")]
public sealed class SchedulesController(ScheduleApplicationService schedules) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BackupScheduleDto>> List() => schedules.ListAsync();

    [HttpPost]
    public async Task<ActionResult<BackupScheduleDto>> Add(UpsertScheduleRequest request)
    {
        try { return await schedules.AddAsync(request); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BackupScheduleDto>> Update(Guid id, UpsertScheduleRequest request)
    {
        try
        {
            var result = await schedules.UpdateAsync(id, request);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("{id:guid}/enable")]
    public Task<IActionResult> Enable(Guid id) => SetEnabled(id, true);

    [HttpPost("{id:guid}/disable")]
    public Task<IActionResult> Disable(Guid id) => SetEnabled(id, false);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        return await schedules.RemoveAsync(id) ? NoContent() : NotFound();
    }

    private async Task<IActionResult> SetEnabled(Guid id, bool enabled)
    {
        return await schedules.SetEnabledAsync(id, enabled) ? NoContent() : NotFound();
    }
}
