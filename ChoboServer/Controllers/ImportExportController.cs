using Chobo.Contracts;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
public sealed class ImportExportController(IExportImportService exports) : ControllerBase
{
    [HttpGet(ChoboApi.ApiPrefix + "/data/export")]
    public Task<ExportEnvelope> ExportData() => exports.ExportAsync(configOnly: false);

    [HttpPost(ChoboApi.ApiPrefix + "/data/import")]
    public async Task<ActionResult<ImportResultDto>> ImportData(ExportEnvelope envelope)
    {
        try
        {
            return await exports.ImportAsync(envelope, configOnly: false);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }

    [HttpGet(ChoboApi.ApiPrefix + "/config/export")]
    public Task<ExportEnvelope> ExportConfig() => exports.ExportAsync(configOnly: true);

    [HttpPost(ChoboApi.ApiPrefix + "/config/import")]
    public async Task<ActionResult<ImportResultDto>> ImportConfig(ExportEnvelope envelope)
    {
        try
        {
            return await exports.ImportAsync(envelope, configOnly: true);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }
}
