using ChoboServer.Data;

namespace ChoboServer.Repositories;

public interface IScheduleRepository
{
    Task<List<BackupScheduleEntity>> ListActiveAsync();
    Task<BackupScheduleEntity?> FindActiveAsync(Guid id);
    Task<BackupScheduleEntity?> FindAsync(Guid id);
    Task<bool> PolicyExistsAsync(Guid policyId);
    Task AddAsync(BackupScheduleEntity schedule);
}

