using ChoboServer.Data;

namespace ChoboServer.Repositories;

public interface IPolicyRepository
{
    Task<List<BackupPolicyEntity>> ListActiveAsync();
    Task<List<BackupPolicyEntity>> ListAsync(bool includeDeleted);
    Task<BackupPolicyEntity?> FindActiveAsync(Guid id);
    Task<BackupPolicyEntity?> FindAsync(Guid id);
    Task AddAsync(BackupPolicyEntity policy);
}

