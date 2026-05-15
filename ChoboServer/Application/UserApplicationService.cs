using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;

namespace ChoboServer.Application;

public sealed class UserApplicationService(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    ITokenService tokens,
    IAuditService audit)
{
    public async Task<IReadOnlyList<UserDto>> ListAsync() =>
        (await users.ListAsync()).Select(ToDto).ToList();

    public async Task<IReadOnlyList<AccessTokenDto>?> ListTokensAsync(Guid userId)
    {
        var user = await users.FindAsync(userId);
        if (user is null)
        {
            return null;
        }

        return (await users.ListTokensAsync(userId)).Select(ToDto).ToList();
    }

    public async Task<CreateUserResponse> AddAsync(CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            throw new ArgumentException("UserName is required.");
        }

        var rawToken = TokenService.GenerateToken();
        var user = new UserEntity { UserName = request.UserName.Trim() };
        await users.AddAsync(user);
        await unitOfWork.SaveChangesAsync();

        await users.AddTokenAsync(tokens.CreateToken(user.Id, "default", rawToken));
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("create", AuditEntityType.User, user.Id.ToString(), AuditDetails.Change(null, ToDto(user)));
        return new CreateUserResponse(user.Id, user.UserName, rawToken);
    }

    public async Task<CreateAccessTokenResponse?> AddTokenAsync(Guid userId, CreateAccessTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required.");
        }

        var user = await users.FindAsync(userId);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        var rawToken = TokenService.GenerateToken();
        var token = tokens.CreateToken(user.Id, request.Name.Trim(), rawToken);
        await users.AddTokenAsync(token);
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("create", AuditEntityType.AccessToken, token.Id.ToString(), AuditDetails.Change(null, ToDto(token)));
        return new CreateAccessTokenResponse(token.Id, user.Id, token.Name, rawToken);
    }

    public async Task<bool> RemoveTokenAsync(Guid userId, Guid tokenId)
    {
        var token = await users.FindTokenAsync(userId, tokenId);
        if (token is null)
        {
            return false;
        }

        var previous = ToDto(token);
        token.IsActive = false;
        token.DeactivatedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("deactivate", AuditEntityType.AccessToken, token.Id.ToString(), AuditDetails.Deactivation(previous, ToDto(token)));
        return true;
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        var user = await users.FindAsync(id);
        if (user is null)
        {
            return false;
        }

        var previous = ToDto(user);
        user.IsActive = false;
        user.DeactivatedAt = DateTimeOffset.UtcNow;
        await users.DeactivateTokensAsync(id);
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("deactivate", AuditEntityType.User, id.ToString(), AuditDetails.Deactivation(previous, ToDto(user)));
        return true;
    }

    private static UserDto ToDto(UserEntity x) =>
        new(x.Id, x.UserName, x.IsActive, x.CreatedAt, x.DeactivatedAt);

    private static AccessTokenDto ToDto(AccessTokenEntity x) =>
        new(x.Id, x.UserId, x.Name, x.IsActive, x.CreatedAt, x.DeactivatedAt);
}
