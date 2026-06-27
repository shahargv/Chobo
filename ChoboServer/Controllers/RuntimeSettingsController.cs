using Chobo.Contracts;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/settings")]
public sealed class RuntimeSettingsController(IRuntimeSettingsService settings) : ControllerBase
{
    [HttpGet]
    public RuntimeSettingsListDto List() => settings.List();

    [HttpGet("{*key}")]
    public ActionResult<RuntimeSettingDto> Get(string key)
    {
        try
        {
            return settings.Get(key);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse(ex.Message));
        }
    }

    [HttpPut("{*key}")]
    public async Task<ActionResult<RuntimeSettingUpdateResult>> Set(string key, UpdateRuntimeSettingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await settings.SetAsync(key, request.Value, cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (FormatException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }

    [HttpDelete("{*key}")]
    public async Task<ActionResult<RuntimeSettingUpdateResult>> Unset(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await settings.UnsetAsync(key, cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse(ex.Message));
        }
    }

    [HttpPost("reload")]
    public RuntimeSettingsReloadResult Reload() => settings.Reload();
}