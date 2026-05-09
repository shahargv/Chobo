using Chobo.Contracts;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
public sealed class ImportExportController(ExportImportService exports, AuditService audit) : ControllerBase
{
    [HttpGet(ChoboApi.ApiPrefix + "/data/export")]
    public Task<ExportEnvelope> ExportData() => exports.ExportAsync(configOnly: false);

    [HttpPost(ChoboApi.ApiPrefix + "/data/import")]
    public async Task<IActionResult> ImportData(ExportEnvelope envelope)
    {
        try
        {
            await exports.ImportAsync(envelope, configOnly: false);
            await audit.RecordAsync("import", "data", null, new { envelope.ExportVersion, envelope.SchemaVersion });
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }

    [HttpGet(ChoboApi.ApiPrefix + "/config/export")]
    public Task<ExportEnvelope> ExportConfig() => exports.ExportAsync(configOnly: true);

    [HttpPost(ChoboApi.ApiPrefix + "/config/import")]
    public async Task<IActionResult> ImportConfig(ExportEnvelope envelope)
    {
        try
        {
            await exports.ImportAsync(envelope, configOnly: true);
            await audit.RecordAsync("import", "config", null, new { envelope.ExportVersion, envelope.SchemaVersion });
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }
}
