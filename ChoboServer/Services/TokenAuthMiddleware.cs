using Chobo.Contracts;

namespace ChoboServer.Services;

public sealed class TokenAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITokenService tokenService, ActorContext actor, Serilog.ILogger logger)
    {
        var log = logger.ForContext<TokenAuthMiddleware>();
        if (!context.Request.Path.StartsWithSegments("/api") || IsAnonymousSetupEndpoint(context))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            log.Warning("Rejecting request {Method} {Path}: missing bearer token.", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Missing bearer token."));
            return;
        }

        try
        {
            var token = header["Bearer ".Length..].Trim();
            var lookupHash = TokenService.HashTokenLookup(token);
            log.Debug("Authenticating request {Method} {Path} with token fingerprint {LookupFingerprint} length {TokenLength}.", context.Request.Method, context.Request.Path, TokenService.Fingerprint(lookupHash), token.Length);
            var (user, _) = await tokenService.AuthenticateAsync(token);
            actor.UserId = user.Id;
            actor.ActorName = user.UserName;
            log.Information("Authenticated request {Method} {Path} as {ActorName} ({ActorUserId}).", context.Request.Method, context.Request.Path, actor.ActorName, actor.UserId);
        }
        catch (UnauthorizedAccessException)
        {
            log.Warning("Rejecting request {Method} {Path}: invalid bearer token.", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Invalid bearer token."));
            return;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Authentication failed unexpectedly for request {Method} {Path}.", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Authentication failed."));
            return;
        }

        await next(context);
    }

    private static bool IsAnonymousSetupEndpoint(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (string.Equals(path, "/api/v1/server/install/status", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(path, "/api/v1/server/install", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(context.Request.Method);
    }
}
