using ChoboServer.Data;

namespace ChoboServer.Repositories;

public interface IPolicyRepository
{
    Task<List<BackupPolicyEntity>> ListActiveAsync();
    Task<BackupPolicyEntity?> FindActiveAsync(Guid id);
    Task<BackupPolicyEntity?> FindAsync(Guid id);
    Task AddAsync(BackupPolicyEntity policy);
}

