namespace Chobo.Contracts;

public enum ClusterMode { SingleInstance, Cluster }

public sealed record AccessNodeDto(Guid Id, string Host, int Port, bool UseTls);

public sealed record ClusterDto(Guid Id, string Name, ClusterMode Mode, IReadOnlyList<AccessNodeDto> AccessNodes, int? BackupRestoreMaxDop, string? ClickHouseClusterName, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record UpsertClusterRequest(string Name, ClusterMode Mode, IReadOnlyList<UpsertAccessNodeRequest> AccessNodes, string? UserName, string? Password, int? BackupRestoreMaxDop = null, string? ClickHouseClusterName = null);

public sealed record UpsertAccessNodeRequest(string Host, int Port = 9000, bool UseTls = false);

public sealed record ClickHouseClusterNamesDto(Guid ClusterId, IReadOnlyList<string> Names);

public sealed record ClickHouseClusterTopologyDto(Guid ClusterId, IReadOnlyList<ClickHouseClusterShardDto> Shards);

public sealed record ClickHouseClusterShardDto(int ShardNumber, string? ShardName, int ReplicaNumber, string Host, int Port, bool UseTls, int ErrorsCount);

