using ChoboServer.Data;

namespace ChoboServer.Repositories;

public interface IClusterRepository
{
    Task<List<ClickHouseClusterEntity>> ListActiveAsync();
    Task<ClickHouseClusterEntity?> FindActiveAsync(Guid id);
    Task<ClickHouseClusterEntity?> FindAsync(Guid id);
    Task AddAsync(ClickHouseClusterEntity cluster);
    Task AddNodesAsync(IEnumerable<ClickHouseAccessNodeEntity> nodes);
    void RemoveNodes(IEnumerable<ClickHouseAccessNodeEntity> nodes);
}
