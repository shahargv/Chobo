namespace Chobo.Contracts;

public sealed record SeedMissingBackupOperationRequest(
    Guid SourceClusterId,
    Guid TargetId,
    string Database,
    string Table,
    int ShardCount = 1);
