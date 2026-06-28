using ChoboServer.Data;

namespace ChoboServer.Repositories;

public interface ITargetRepository
{
    Task<List<BackupTargetEntity>> ListActiveAsync();
    Task<List<BackupTargetEntity>> ListAsync(bool includeDeleted);
    Task<BackupTargetEntity?> FindActiveAsync(Guid id);
    Task<BackupTargetEntity?> FindAsync(Guid id);
    Task AddAsync(BackupTargetEntity target);
}

