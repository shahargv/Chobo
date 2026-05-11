using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix + "/users")]
public sealed class UsersController(UserApplicationService users) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<UserDto>> List() => users.ListAsync();

    [HttpPost]
    public async Task<ActionResult<CreateUserResponse>> Add(CreateUserRequest request)
    {
        try { return await users.AddAsync(request); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpGet("{id:guid}/tokens")]
    public async Task<ActionResult<IReadOnlyList<AccessTokenDto>>> ListTokens(Guid id)
    {
        var result = await users.ListTokensAsync(id);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{id:guid}/tokens")]
    public async Task<ActionResult<CreateAccessTokenResponse>> AddToken(Guid id, CreateAccessTokenRequest request)
    {
        try
        {
            var result = await users.AddTokenAsync(id, request);
            return result is null ? NotFound() : result;
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpDelete("{id:guid}/tokens/{tokenId:guid}")]
    public async Task<IActionResult> RemoveToken(Guid id, Guid tokenId)
    {
        return await users.RemoveTokenAsync(id, tokenId) ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        return await users.RemoveAsync(id) ? NoContent() : NotFound();
    }
}
