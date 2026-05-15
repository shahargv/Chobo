namespace Chobo.Contracts;

public sealed record StorageConnectionTestResult(Guid TargetId, BackupTargetType TargetType, bool Succeeded, string Message);

public sealed record ClusterConnectionTestResult(Guid ClusterId, bool Succeeded, string Message);
