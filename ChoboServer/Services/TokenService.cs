using System.Security.Cryptography;
using System.Text;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Services;

public sealed class TokenService(ChoboDbContext db, Serilog.ILogger logger)
{
    private readonly Serilog.ILogger _logger = logger.ForContext<TokenService>();

    public async Task<(UserEntity User, AccessTokenEntity Token)> AuthenticateAsync(string token)
    {
        var lookupHash = HashTokenLookup(token);
        var candidates = await LoadLookupCandidatesAsync(lookupHash);

        if (candidates.Count == 0)
        {
            _logger.Warning("No active access-token candidate matched lookup fingerprint {LookupFingerprint} length {TokenLength}; checking legacy tokens without lookup hashes.", Fingerprint(lookupHash), token.Length);
            candidates = await LoadLegacyCandidatesAsync();
        }

        if (await TryAuthenticateCandidateAsync(token, lookupHash, candidates) is { } authenticated)
        {
            return authenticated;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        candidates = await LoadLookupCandidatesAsync(lookupHash);
        if (candidates.Count == 0)
        {
            candidates = await LoadLegacyCandidatesAsync();
        }
        if (candidates.Count == 0)
        {
            candidates = await LoadAllActiveCandidatesAsync();
        }

        if (await TryAuthenticateCandidateAsync(token, lookupHash, candidates) is { } retryAuthenticated)
        {
            _logger.Information("Access-token lookup fingerprint {LookupFingerprint} authenticated after retry.", Fingerprint(lookupHash));
            return retryAuthenticated;
        }

        throw new UnauthorizedAccessException("Invalid access token.");
    }

    private async Task<List<AccessTokenEntity>> LoadLookupCandidatesAsync(string lookupHash) =>
        (await db.AccessTokens
            .AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.IsActive && x.TokenLookupHash == lookupHash)
            .ToListAsync())
        .Where(x => x.User is { IsActive: true })
        .ToList();

    private async Task<List<AccessTokenEntity>> LoadLegacyCandidatesAsync() =>
        (await db.AccessTokens
            .AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.IsActive && x.TokenLookupHash == "")
            .ToListAsync())
        .Where(x => x.User is { IsActive: true })
        .ToList();

    private async Task<List<AccessTokenEntity>> LoadAllActiveCandidatesAsync() =>
        (await db.AccessTokens
            .AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.IsActive)
            .ToListAsync())
        .Where(x => x.User is { IsActive: true })
        .ToList();

    private async Task<(UserEntity User, AccessTokenEntity Token)?> TryAuthenticateCandidateAsync(
        string token,
        string lookupHash,
        IReadOnlyList<AccessTokenEntity> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (HashToken(token, candidate.Salt) == candidate.TokenHash)
            {
                if (candidate.TokenLookupHash == "")
                {
                    await db.AccessTokens
                        .Where(x => x.Id == candidate.Id && x.TokenLookupHash == "")
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.TokenLookupHash, lookupHash));
                    candidate.TokenLookupHash = lookupHash;
                }

                return (candidate.User!, candidate);
            }
        }

        return null;
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

    public static string Fingerprint(string hash) =>
        hash.Length <= 12 ? hash : hash[..12];
}
