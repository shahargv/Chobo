using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix)]
public sealed class BackupsController(
    BackupApplicationService backups,
    IBackupStorageManifestService backupStorageManifests) : ControllerBase
{
    [HttpPost("backups/manual")]
    public async Task<ActionResult<BackupDto>> Manual(ManualBackupRequest request, CancellationToken cancellationToken)
    {
        try { return await backups.ManualAsync(request, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpGet("backups")]
    public Task<IReadOnlyList<BackupDto>> List(
        [FromQuery] Guid? policyId,
        [FromQuery] string? clusterName,
        [FromQuery] string? tableName,
        [FromQuery] BackupRunStatus? status,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] bool includeTables = true,
        CancellationToken cancellationToken = default) =>
        backups.ListAsync(policyId, clusterName, tableName, status, from, to, includeTables, cancellationToken);

    [HttpGet("backups/{id:guid}")]
    public async Task<ActionResult<BackupDto>> Get(Guid id, [FromQuery] bool includeTables = true, CancellationToken cancellationToken = default)
    {
        var result = await backups.GetAsync(id, includeTables, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpPost("backups/{id:guid}/pin")]
    public async Task<ActionResult<BackupDto>> Pin(Guid id, CancellationToken cancellationToken)
    {
        var result = await backups.PinAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpPost("backups/{id:guid}/unpin")]
    public async Task<ActionResult<BackupDto>> Unpin(Guid id, CancellationToken cancellationToken)
    {
        var result = await backups.UnpinAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpPost("backups/{id:guid}/cancel")]
    public async Task<ActionResult<BackupDto>> Cancel(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await backups.CancelAsync(id, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }
    [HttpDelete("backups/{id:guid}")]
    public async Task<ActionResult<BackupDto>> Delete(Guid id, [FromQuery] bool force, [FromQuery] bool confirmDestructive, CancellationToken cancellationToken)
    {
        try
        {
            var result = await backups.RequestDeleteAsync(id, force, confirmDestructive, cancellationToken);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("backups/recover/from-path")]
    public async Task<ActionResult<BackupMetadataRecoveryResult>> RecoverFromPath(RecoverBackupMetadataFromPathRequest request, CancellationToken cancellationToken)
    {
        try { return await backupStorageManifests.RecoverFromPathAsync(request, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("backups/recover/scan")]
    public async Task<ActionResult<BackupMetadataRecoveryResult>> RecoverFromScan(RecoverBackupMetadataScanRequest request, CancellationToken cancellationToken)
    {
        try { return await backupStorageManifests.RecoverFromScanAsync(request, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }
}
