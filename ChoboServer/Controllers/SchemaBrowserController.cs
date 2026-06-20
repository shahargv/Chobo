using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/schema")]
public sealed class SchemaBrowserController(SchemaBrowserApplicationService schemas) : ControllerBase
{
    [HttpGet("backups")]
    public Task<IReadOnlyList<SchemaBackupSummaryDto>> ListBackups([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken cancellationToken) =>
        schemas.ListBackupsAsync(from, to, cancellationToken);

    [HttpGet("backups/{backupId:guid}")]
    public async Task<ActionResult<SchemaBackupDto>> GetBackupSchema(Guid backupId, CancellationToken cancellationToken)
    {
        var result = await schemas.GetBackupSchemaAsync(backupId, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpGet("backups/{backupId:guid}/export")]
    public async Task<IActionResult> ExportBackupSchema(Guid backupId, [FromQuery] string? database, CancellationToken cancellationToken)
    {
        var result = await schemas.ExportSqlAsync(backupId, database, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        var fileName = string.IsNullOrWhiteSpace(database) ? $"schema-{backupId:N}.sql" : $"schema-{backupId:N}-{database}.sql";
        return File(System.Text.Encoding.UTF8.GetBytes(result), "application/sql; charset=utf-8", fileName);
    }
}
