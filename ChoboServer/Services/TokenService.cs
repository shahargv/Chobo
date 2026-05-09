using System.Security.Cryptography;
using System.Text;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Services;

public sealed class TokenService(ChoboDbContext db)
{
    public async Task<(UserEntity User, AccessTokenEntity Token)> AuthenticateAsync(string token)
    {
        var lookupHash = HashTokenLookup(token);
        var candidates = await db.AccessTokens
            .Include(x => x.User)
            .Where(x => x.IsActive && x.TokenLookupHash == lookupHash && x.User != null && x.User.IsActive)
            .ToListAsync();

        if (candidates.Count == 0)
        {
            candidates = await db.AccessTokens
                .Include(x => x.User)
                .Where(x => x.IsActive && x.TokenLookupHash == "" && x.User != null && x.User.IsActive)
                .ToListAsync();
        }

        foreach (var candidate in candidates)
        {
            if (HashToken(token, candidate.Salt) == candidate.TokenHash)
            {
                if (candidate.TokenLookupHash == "")
                {
                    candidate.TokenLookupHash = lookupHash;
                    await db.SaveChangesAsync();
                }

                return (candidate.User!, candidate);
            }
        }

        throw new UnauthorizedAccessException("Invalid access token.");
    }

    public AccessTokenEntity CreateToken(Guid userId, string name, string rawToken)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new AccessTokenEntity
        {
            UserId = userId,
            Name = name,
            Salt = salt,
            TokenHash = HashToken(rawToken, salt),
            TokenLookupHash = HashTokenLookup(rawToken)
        };
    }

    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string HashToken(string token, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{token}"));
        return Convert.ToBase64String(bytes);
    }

    public static string HashTokenLookup(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"lookup:{token}"));
        return Convert.ToBase64String(bytes);
    }
}
