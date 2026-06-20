using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/server")]
public sealed class ServerController(ChoboDbContext db, IDatabaseBootstrap bootstrap) : ControllerBase
{
    [HttpGet("version")]
    public async Task<ServerVersionDto> Version()
    {
        var schema = await db.SchemaStates.SingleAsync();
        return new ServerVersionDto(ChoboApi.ProductName, ChoboApi.ProductVersion, ChoboApi.ApiVersion, ChoboApi.SchemaVersion, schema.SchemaVersion);
    }

    [HttpGet("install/status")]
    public Task<InstallStatusDto> InstallStatus(CancellationToken cancellationToken) =>
        bootstrap.GetInstallStatusAsync(cancellationToken);

    [HttpPost("install")]
    public async Task<ActionResult<InstallResponse>> Install(InstallRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await bootstrap.InstallAsync(request.AdminUser, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse(ex.Message));
        }
    }
}
