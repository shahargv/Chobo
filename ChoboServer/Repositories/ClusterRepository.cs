using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Repositories;

public sealed class ClusterRepository(ChoboDbContext db) : IClusterRepository
{
    public Task<List<ClickHouseClusterEntity>> ListActiveAsync() =>
        db.ClickHouseClusters
            .Include(x => x.AccessNodes)
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<List<ClickHouseClusterEntity>> ListAsync(bool includeDeleted) =>
        db.ClickHouseClusters
            .Include(x => x.AccessNodes)
            .Where(x => includeDeleted || !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync();

    public Task<ClickHouseClusterEntity?> FindActiveAsync(Guid id) =>
        db.ClickHouseClusters
            .Include(x => x.AccessNodes)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

    public Task<ClickHouseClusterEntity?> FindAsync(Guid id) =>
        db.ClickHouseClusters
            .Include(x => x.AccessNodes)
            .FirstOrDefaultAsync(x => x.Id == id);

    public async Task AddAsync(ClickHouseClusterEntity cluster) =>
        await db.ClickHouseClusters.AddAsync(cluster);

    public async Task AddNodesAsync(IEnumerable<ClickHouseAccessNodeEntity> nodes) =>
        await db.ClickHouseAccessNodes.AddRangeAsync(nodes);

    public void RemoveNodes(IEnumerable<ClickHouseAccessNodeEntity> nodes) =>
        db.ClickHouseAccessNodes.RemoveRange(nodes);
}
