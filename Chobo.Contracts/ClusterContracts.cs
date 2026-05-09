namespace Chobo.Contracts;

public enum ClusterMode { SingleInstance, Cluster }

public sealed record AccessNodeDto(Guid Id, string Host, int Port, bool UseTls);

public sealed record ClusterDto(Guid Id, string Name, ClusterMode Mode, IReadOnlyList<AccessNodeDto> AccessNodes, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record UpsertClusterRequest(string Name, ClusterMode Mode, IReadOnlyList<UpsertAccessNodeRequest> AccessNodes, string? UserName, string? Password);

public sealed record UpsertAccessNodeRequest(string Host, int Port = 9000, bool UseTls = false);
