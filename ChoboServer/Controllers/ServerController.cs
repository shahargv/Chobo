using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/server")]
public sealed class ServerController(ChoboDbContext db) : ControllerBase
{
    [HttpGet("version")]
    public async Task<ServerVersionDto> Version()
    {
        var schema = await db.SchemaStates.SingleAsync();
        return new ServerVersionDto(ChoboApi.ProductName, ChoboApi.ProductVersion, ChoboApi.ApiVersion, ChoboApi.SchemaVersion, schema.SchemaVersion);
    }
}
