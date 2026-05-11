using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public sealed class TargetRepository(ChoboDbContext db) : ITargetRepository
{
    public Task<List<BackupTargetEntity>> ListActiveAsync() =>
        db.BackupTargets
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<BackupTargetEntity?> FindActiveAsync(Guid id) =>
        db.BackupTargets.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

    public async Task<BackupTargetEntity?> FindAsync(Guid id) =>
        await db.BackupTargets.FindAsync(id);

    public async Task AddAsync(BackupTargetEntity target) =>
        await db.BackupTargets.AddAsync(target);
}

