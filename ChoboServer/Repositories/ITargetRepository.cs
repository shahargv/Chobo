using ChoboServer.Data;

namespace ChoboServer.Repositories;

public interface ITargetRepository
{
    Task<List<BackupTargetEntity>> ListActiveAsync();
    Task<BackupTargetEntity?> FindActiveAsync(Guid id);
    Task<BackupTargetEntity?> FindAsync(Guid id);
    Task AddAsync(BackupTargetEntity target);
}

