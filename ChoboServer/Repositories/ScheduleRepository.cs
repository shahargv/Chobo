using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public sealed class ScheduleRepository(ChoboDbContext db) : IScheduleRepository
{
    public Task<List<BackupScheduleEntity>> ListActiveAsync() =>
        db.BackupSchedules
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<List<BackupScheduleEntity>> ListAsync(bool includeDeleted) =>
        db.BackupSchedules
            .Where(x => includeDeleted || !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<List<BackupScheduleEntity>> ListActiveByPolicyAsync(Guid policyId) =>
        db.BackupSchedules
            .Where(x => x.PolicyId == policyId && !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<BackupScheduleEntity?> FindActiveAsync(Guid id) =>
        db.BackupSchedules.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

    public async Task<BackupScheduleEntity?> FindAsync(Guid id) =>
        await db.BackupSchedules.FindAsync(id);

    public Task<BackupPolicyEntity?> FindActivePolicyAsync(Guid policyId) =>
        db.BackupPolicies.FirstOrDefaultAsync(x => x.Id == policyId && !x.IsDeleted);

    public Task<bool> PolicyExistsAsync(Guid policyId) =>
        db.BackupPolicies.AnyAsync(x => x.Id == policyId && !x.IsDeleted);

    public async Task AddAsync(BackupScheduleEntity schedule) =>
        await db.BackupSchedules.AddAsync(schedule);
}

