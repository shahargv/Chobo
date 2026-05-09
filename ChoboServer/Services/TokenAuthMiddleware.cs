using Chobo.Contracts;

namespace ChoboServer.Services;

public sealed class TokenAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TokenService tokenService, ActorContext actor)
    {
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/openapi"))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Missing bearer token."));
            return;
        }

        try
        {
            var (user, _) = await tokenService.AuthenticateAsync(header["Bearer ".Length..].Trim());
            actor.UserId = user.Id;
            actor.ActorName = user.UserName;
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Invalid bearer token."));
            return;
        }

        await next(context);
    }
}
