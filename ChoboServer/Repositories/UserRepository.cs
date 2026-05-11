using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public sealed class UserRepository(ChoboDbContext db) : IUserRepository
{
    public Task<List<UserEntity>> ListAsync() =>
        db.Users.OrderBy(x => x.UserName).ToListAsync();

    public async Task<UserEntity?> FindAsync(Guid id) =>
        await db.Users.FindAsync(id);

    public Task<List<AccessTokenEntity>> ListTokensAsync(Guid userId) =>
        db.AccessTokens
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<AccessTokenEntity?> FindTokenAsync(Guid userId, Guid tokenId) =>
        db.AccessTokens.FirstOrDefaultAsync(x => x.UserId == userId && x.Id == tokenId);

    public async Task AddAsync(UserEntity user) =>
        await db.Users.AddAsync(user);

    public async Task AddTokenAsync(AccessTokenEntity token) =>
        await db.AccessTokens.AddAsync(token);

    public Task DeactivateTokensAsync(Guid userId) =>
        db.AccessTokens
            .Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.IsActive, false)
                .SetProperty(x => x.DeactivatedAt, DateTimeOffset.UtcNow));
}
