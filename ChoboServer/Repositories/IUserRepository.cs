using ChoboServer.Data;

namespace ChoboServer.Repositories;

public interface IUserRepository
{
    Task<List<UserEntity>> ListAsync();
    Task<UserEntity?> FindAsync(Guid id);
    Task<List<AccessTokenEntity>> ListTokensAsync(Guid userId);
    Task<AccessTokenEntity?> FindTokenAsync(Guid userId, Guid tokenId);
    Task AddAsync(UserEntity user);
    Task AddTokenAsync(AccessTokenEntity token);
    Task DeactivateTokensAsync(Guid userId);
}
