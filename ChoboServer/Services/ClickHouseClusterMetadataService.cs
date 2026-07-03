using System.Collections.Concurrent;
using System.Diagnostics;
using ChoboServer.Data;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public sealed record ClickHouseTablePlacement(ClickHouseTableInfo Table, ClickHouseShardReplicaInfo Node);

public sealed record ClickHouseClusterMetadataNodeFailure(ClickHouseShardReplicaInfo Node, string Error);

public sealed record ClickHouseClusterMetadataSnapshot(
    Guid ClusterId,
    string ClusterName,
    DateTimeOffset RefreshedAt,
    IReadOnlyList<string> ClickHouseClusterNames,
    IReadOnlyList<ClickHouseShardReplicaInfo> Topology,
    IReadOnlyList<ClickHouseTablePlacement> Placements,
    IReadOnlyList<ClickHouseClusterMetadataNodeFailure> NodeFailures,
    bool IsComplete);

public interface IClickHouseClusterMetadataService
{
    Task<ClickHouseClusterMetadataSnapshot> GetAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken = default);
    Task<ClickHouseClusterMetadataSnapshot> RefreshAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken = default);
    void Invalidate(Guid clusterId);
}

public sealed class ClickHouseClusterMetadataService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ChoboClusterMetadataOptions> options,
    TimeProvider timeProvider,
    Serilog.ILogger logger) : IClickHouseClusterMetadataService
{
    private readonly ConcurrentDictionary<Guid, CacheEntry> _entries = new();
    private readonly Serilog.ILogger _logger = logger.ForContext<ClickHouseClusterMetadataService>();

    public async Task<ClickHouseClusterMetadataSnapshot> GetAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken = default)
    {
        var entry = _entries.GetOrAdd(cluster.Id, _ => new CacheEntry());
        var cached = entry.Snapshot;
        if (cached is not null && IsFresh(cached))
        {
            return cached;
        }

        return await RefreshCoreAsync(cluster, entry, force: false, cancellationToken);
    }

    public async Task<ClickHouseClusterMetadataSnapshot> RefreshAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken = default)
    {
        var entry = _entries.GetOrAdd(cluster.Id, _ => new CacheEntry());
        return await RefreshCoreAsync(cluster, entry, force: true, cancellationToken);
    }

    public void Invalidate(Guid clusterId)
    {
        if (_entries.TryRemove(clusterId, out _))
        {
            _logger.Information("Invalidated ClickHouse metadata cache for cluster {ClusterId}.", clusterId);
        }
    }

    private async Task<ClickHouseClusterMetadataSnapshot> RefreshCoreAsync(ClickHouseClusterEntity cluster, CacheEntry entry, bool force, CancellationToken cancellationToken)
    {
        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            var cached = entry.Snapshot;
            if (!force && cached is not null && IsFresh(cached))
            {
                return cached;
            }

            var refreshed = await BuildSnapshotAsync(cluster, cancellationToken);
            if (refreshed.IsComplete)
            {
                entry.Snapshot = refreshed;
                return refreshed;
            }

            return entry.Snapshot is { } previous && IsFresh(previous)
                ? previous
                : refreshed;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private async Task<ClickHouseClusterMetadataSnapshot> BuildSnapshotAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        var clickHouse = scope.ServiceProvider.GetRequiredService<IClickHouseAdapter>();
        IReadOnlyList<string> clusterNames = [];
        IReadOnlyList<ClickHouseShardReplicaInfo> topology = [];
        var placements = new List<ClickHouseTablePlacement>();
        var failures = new List<ClickHouseClusterMetadataNodeFailure>();

        clusterNames = await clickHouse.GetClusterNamesAsync(cluster, cancellationToken);
        topology = await clickHouse.GetTopologyAsync(cluster, cancellationToken);
        var orderedNodes = topology
            .OrderBy(x => x.ShardNumber)
            .ThenBy(x => x.ReplicaNumber)
            .ThenBy(x => x.Host, StringComparer.Ordinal)
            .ThenBy(x => x.Port)
            .ToList();

        if (orderedNodes.Count == 0)
        {
            var fallbackNode = FirstTopologyNode(cluster);
            var nodeStopwatch = Stopwatch.StartNew();
            var tables = await clickHouse.GetTablesAsync(cluster, cancellationToken);
            nodeStopwatch.Stop();
            placements.AddRange(tables.Select(table => new ClickHouseTablePlacement(table, fallbackNode)));
            _logger.Information("ClickHouse metadata node refresh succeeded for cluster {ClusterId} ({ClusterName}) shard {ShardNumber} replica {ReplicaNumber} on {Host}:{Port}; tables={TableCount}; elapsedMs={ElapsedMilliseconds}.", cluster.Id, cluster.Name, fallbackNode.ShardNumber, fallbackNode.ReplicaNumber, fallbackNode.Host, fallbackNode.Port, tables.Count, nodeStopwatch.ElapsedMilliseconds);
        }
        else
        {
            foreach (var node in orderedNodes)
            {
                var nodeStopwatch = Stopwatch.StartNew();
                try
                {
                    var tables = await clickHouse.GetTablesAsync(node.Endpoint, cluster, cancellationToken);
                    placements.AddRange(tables.Select(table => new ClickHouseTablePlacement(table, node)));
                    nodeStopwatch.Stop();
                    _logger.Information("ClickHouse metadata node refresh succeeded for cluster {ClusterId} ({ClusterName}) shard {ShardNumber} replica {ReplicaNumber} on {Host}:{Port}; tables={TableCount}; elapsedMs={ElapsedMilliseconds}.", cluster.Id, cluster.Name, node.ShardNumber, node.ReplicaNumber, node.Host, node.Port, tables.Count, nodeStopwatch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    nodeStopwatch.Stop();
                    failures.Add(new ClickHouseClusterMetadataNodeFailure(node, ex.Message));
                    _logger.Warning(ex, "ClickHouse metadata node refresh failed for cluster {ClusterId} ({ClusterName}) shard {ShardNumber} replica {ReplicaNumber} on {Host}:{Port}; elapsedMs={ElapsedMilliseconds}.", cluster.Id, cluster.Name, node.ShardNumber, node.ReplicaNumber, node.Host, node.Port, nodeStopwatch.ElapsedMilliseconds);
                }
            }
        }

        total.Stop();
        var snapshot = new ClickHouseClusterMetadataSnapshot(
            cluster.Id,
            cluster.Name,
            timeProvider.GetUtcNow(),
            clusterNames,
            topology,
            placements
                .OrderBy(x => x.Table.Database, StringComparer.Ordinal)
                .ThenBy(x => x.Table.Table, StringComparer.Ordinal)
                .ThenBy(x => x.Node.ShardNumber)
                .ThenBy(x => x.Node.ReplicaNumber)
                .ToList(),
            failures,
            failures.Count == 0);
        _logger.Information("ClickHouse metadata cluster refresh completed for cluster {ClusterId} ({ClusterName}); nodes={NodeCount}; failedNodes={FailedNodeCount}; tablePlacements={TablePlacementCount}; topology={TopologyCount}; clusterNames={ClusterNameCount}; elapsedMs={ElapsedMilliseconds}; cacheReplaced={CacheReplaced}.", cluster.Id, cluster.Name, orderedNodes.Count == 0 ? 1 : orderedNodes.Count, failures.Count, snapshot.Placements.Count, topology.Count, clusterNames.Count, total.ElapsedMilliseconds, snapshot.IsComplete);
        return snapshot;
    }

    private bool IsFresh(ClickHouseClusterMetadataSnapshot snapshot)
    {
        var duration = options.CurrentValue.CacheDuration <= TimeSpan.Zero
            ? TimeSpan.FromHours(1)
            : options.CurrentValue.CacheDuration;
        return timeProvider.GetUtcNow() - snapshot.RefreshedAt <= duration;
    }

    private static ClickHouseShardReplicaInfo FirstTopologyNode(ClickHouseClusterEntity cluster)
    {
        if (cluster.AccessNodes.Count == 0)
        {
            throw new InvalidOperationException("Cluster has no access nodes.");
        }

        var node = cluster.AccessNodes[0];
        return new ClickHouseShardReplicaInfo(1, "single", 1, node.Host, node.Port, node.UseTls, 0);
    }

    private sealed class CacheEntry
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public ClickHouseClusterMetadataSnapshot? Snapshot { get; set; }
    }
}
