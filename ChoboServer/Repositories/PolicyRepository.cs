using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public sealed class PolicyRepository(ChoboDbContext db) : IPolicyRepository
{
    public Task<List<BackupPolicyEntity>> ListActiveAsync() =>
        db.BackupPolicies
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<BackupPolicyEntity?> FindActiveAsync(Guid id) =>
        db.BackupPolicies.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

    public async Task<BackupPolicyEntity?> FindAsync(Guid id) =>
        await db.BackupPolicies.FindAsync(id);

    public async Task AddAsync(BackupPolicyEntity policy) =>
        await db.BackupPolicies.AddAsync(policy);
}

