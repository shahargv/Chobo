namespace Chobo.Contracts;

public sealed record UserDto(Guid Id, string UserName, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record CreateUserRequest(string UserName);

public sealed record CreateUserResponse(Guid UserId, string UserName, string AccessToken);

public sealed record AccessTokenDto(Guid Id, Guid UserId, string Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record CreateAccessTokenRequest(string Name);

public sealed record CreateAccessTokenResponse(Guid TokenId, Guid UserId, string Name, string AccessToken);
