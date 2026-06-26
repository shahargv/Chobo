using Chobo.Contracts;
using ChoboServer.Services;

namespace ChoboServer.Application;

public interface IBackupRestoreConcurrencyCoordinator
{
    bool TryReserveQueueItem(
        BackupRestoreQueueKind kind,
        Guid operationId,
        Guid shardId,
        Guid clusterId,
        int logicalShardNumber,
        bool force,
        int clusterLimit,
        int shardLimit,
        int persistedClusterRunning,
        int persistedShardRunning,
        string? destinationPath);

    void ConfirmQueueItemStarted(BackupRestoreQueueKind kind, Guid shardId);

    bool TryReserveNode(
        BackupRestoreQueueKind kind,
        Guid shardId,
        Guid clusterId,
        ClickHouseNodeEndpoint endpoint,
        bool force,
        int nodeLimit,
        int persistedNodeRunning);

    void ConfirmNodeStarted(BackupRestoreQueueKind kind, Guid shardId);
    void ReleaseQueueItem(BackupRestoreQueueKind kind, Guid shardId);
    void ReleaseNode(BackupRestoreQueueKind kind, Guid shardId);
    void ReleaseOperation(BackupRestoreQueueKind kind, Guid operationId);
}

public sealed class BackupRestoreConcurrencyCoordinator : IBackupRestoreConcurrencyCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _pendingClusterCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingShardCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingNodeCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueueLease> _queueLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeLease> _nodeLeases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _destinationLeases = new(StringComparer.Ordinal);

    public bool TryReserveQueueItem(
        BackupRestoreQueueKind kind,
        Guid operationId,
        Guid shardId,
        Guid clusterId,
        int logicalShardNumber,
        bool force,
        int clusterLimit,
        int shardLimit,
        int persistedClusterRunning,
        int persistedShardRunning,
        string? destinationPath)
    {
        var itemKey = ItemKey(kind, shardId);
        var clusterKey = ClusterKey(clusterId);
        var shardKey = ShardKey(clusterId, logicalShardNumber);
        var destinationKey = DestinationKey(kind, destinationPath);

        lock (_gate)
        {
            if (_queueLeases.ContainsKey(itemKey) ||
                (destinationKey is not null && _destinationLeases.Contains(destinationKey)))
            {
                return false;
            }

            var pendingCapacity = !force;
            if (pendingCapacity)
            {
                if (persistedClusterRunning + Count(_pendingClusterCounts, clusterKey) >= Math.Max(1, clusterLimit) ||
                    persistedShardRunning + Count(_pendingShardCounts, shardKey) >= Math.Max(1, shardLimit))
                {
                    return false;
                }
            }

            _queueLeases[itemKey] = new QueueLease(operationId, clusterKey, shardKey, destinationKey, pendingCapacity);
            if (destinationKey is not null)
            {
                _destinationLeases.Add(destinationKey);
            }
            if (pendingCapacity)
            {
                Increment(_pendingClusterCounts, clusterKey);
                Increment(_pendingShardCounts, shardKey);
            }

            return true;
        }
    }

    public void ConfirmQueueItemStarted(BackupRestoreQueueKind kind, Guid shardId)
    {
        var itemKey = ItemKey(kind, shardId);
        lock (_gate)
        {
            if (!_queueLeases.TryGetValue(itemKey, out var lease) || !lease.PendingCapacity)
            {
                return;
            }

            Decrement(_pendingClusterCounts, lease.ClusterKey);
            Decrement(_pendingShardCounts, lease.ShardKey);
            _queueLeases[itemKey] = lease with { PendingCapacity = false };
        }
    }

    public bool TryReserveNode(
        BackupRestoreQueueKind kind,
        Guid shardId,
        Guid clusterId,
        ClickHouseNodeEndpoint endpoint,
        bool force,
        int nodeLimit,
        int persistedNodeRunning)
    {
        var itemKey = ItemKey(kind, shardId);
        var nodeKey = NodeKey(clusterId, endpoint);

        lock (_gate)
        {
            if (_nodeLeases.ContainsKey(itemKey))
            {
                return true;
            }

            var pendingCapacity = !force;
            if (pendingCapacity && persistedNodeRunning + Count(_pendingNodeCounts, nodeKey) >= Math.Max(1, nodeLimit))
            {
                return false;
            }

            _nodeLeases[itemKey] = new NodeLease(nodeKey, pendingCapacity);
            if (pendingCapacity)
            {
                Increment(_pendingNodeCounts, nodeKey);
            }

            return true;
        }
    }

    public void ConfirmNodeStarted(BackupRestoreQueueKind kind, Guid shardId)
    {
        var itemKey = ItemKey(kind, shardId);
        lock (_gate)
        {
            if (!_nodeLeases.TryGetValue(itemKey, out var lease) || !lease.PendingCapacity)
            {
                return;
            }

            Decrement(_pendingNodeCounts, lease.NodeKey);
            _nodeLeases[itemKey] = lease with { PendingCapacity = false };
        }
    }

    public void ReleaseQueueItem(BackupRestoreQueueKind kind, Guid shardId)
    {
        var itemKey = ItemKey(kind, shardId);
        lock (_gate)
        {
            ReleaseQueueItemNoLock(itemKey);
            ReleaseNodeNoLock(itemKey);
        }
    }

    public void ReleaseNode(BackupRestoreQueueKind kind, Guid shardId)
    {
        var itemKey = ItemKey(kind, shardId);
        lock (_gate)
        {
            ReleaseNodeNoLock(itemKey);
        }
    }

    public void ReleaseOperation(BackupRestoreQueueKind kind, Guid operationId)
    {
        lock (_gate)
        {
            var itemKeys = _queueLeases
                .Where(x => x.Value.OperationId == operationId && x.Key.StartsWith($"{(int)kind}:", StringComparison.Ordinal))
                .Select(x => x.Key)
                .ToList();
            foreach (var itemKey in itemKeys)
            {
                ReleaseQueueItemNoLock(itemKey);
                ReleaseNodeNoLock(itemKey);
            }
        }
    }

    private void ReleaseQueueItemNoLock(string itemKey)
    {
        if (!_queueLeases.Remove(itemKey, out var lease))
        {
            return;
        }

        if (lease.DestinationKey is not null)
        {
            _destinationLeases.Remove(lease.DestinationKey);
        }
        if (lease.PendingCapacity)
        {
            Decrement(_pendingClusterCounts, lease.ClusterKey);
            Decrement(_pendingShardCounts, lease.ShardKey);
        }
    }

    private void ReleaseNodeNoLock(string itemKey)
    {
        if (!_nodeLeases.Remove(itemKey, out var lease) || !lease.PendingCapacity)
        {
            return;
        }

        Decrement(_pendingNodeCounts, lease.NodeKey);
    }

    private static int Count(Dictionary<string, int> counts, string key) =>
        counts.TryGetValue(key, out var count) ? count : 0;

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = Count(counts, key) + 1;

    private static void Decrement(Dictionary<string, int> counts, string key)
    {
        if (!counts.TryGetValue(key, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            counts.Remove(key);
            return;
        }

        counts[key] = count - 1;
    }

    private static string ItemKey(BackupRestoreQueueKind kind, Guid shardId) => $"{(int)kind}:{shardId:N}";
    private static string ClusterKey(Guid clusterId) => clusterId.ToString("N");
    private static string ShardKey(Guid clusterId, int shardNumber) => $"{clusterId:N}:shard:{shardNumber}";
    private static string NodeKey(Guid clusterId, ClickHouseNodeEndpoint endpoint) => $"{clusterId:N}:node:{endpoint.Host.ToLowerInvariant()}:{endpoint.Port}:{endpoint.UseTls}";
    private static string? DestinationKey(BackupRestoreQueueKind kind, string? destinationPath) =>
        string.IsNullOrWhiteSpace(destinationPath) ? null : $"{(int)kind}:destination:{destinationPath.Trim()}";

    private sealed record QueueLease(Guid OperationId, string ClusterKey, string ShardKey, string? DestinationKey, bool PendingCapacity);

    private sealed record NodeLease(string NodeKey, bool PendingCapacity);
}