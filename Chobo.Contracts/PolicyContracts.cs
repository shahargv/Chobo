using System.Text.Json.Serialization;

namespace Chobo.Contracts;

public enum PolicyMatchKind { All, Exact, Wildcard }

public enum PolicySelectorAction { Include, Exclude }

public enum FailedBackupRetentionMode
{
    KeepAndExcludeFromMinBackupsToKeep,
    DeleteByGarbageCollectorAfterFailure
}

public enum BackupContentMode { SchemaAndData, SchemaOnly }

public sealed record BackupRetentionDto(
    int? FullRetentionMinutes,
    int? IncrementalRetentionMinutes,
    int MinBackupsToKeep,
    int MinFullBackupsToKeep)
{
    [JsonPropertyName("retentionMinutes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyRetentionMinutes
    {
        get => null;
        init
        {
            if (value is not { } retentionMinutes)
            {
                return;
            }

            FullRetentionMinutes ??= retentionMinutes;
            IncrementalRetentionMinutes ??= retentionMinutes;
        }
    }
}

public sealed record BackupPolicyDto(
    Guid Id,
    string Name,
    Guid SourceClusterId,
    Guid? TargetId,
    BackupContentMode ContentMode,
    int SelectorJsonVersion,
    PolicySelector Selector,
    BackupRetentionDto? Retention,
    FailedBackupRetentionMode FailedBackupRetentionMode,
    bool IsSystemDefault,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record UpsertPolicyRequest(
    string Name,
    Guid SourceClusterId,
    Guid? TargetId,
    PolicySelector Selector,
    BackupContentMode ContentMode = BackupContentMode.SchemaAndData,
    BackupRetentionDto? Retention = null,
    FailedBackupRetentionMode FailedBackupRetentionMode = FailedBackupRetentionMode.KeepAndExcludeFromMinBackupsToKeep);

public sealed record PolicySelector(int Version, IReadOnlyList<PolicySelectorRule> Rules)
{
    public static PolicySelector Empty => new(1, [PolicySelectorRule.IncludeAll]);
}

public sealed record PolicySelectorRule(PolicySelectorAction Action, SelectorPattern Database, SelectorPattern Table)
{
    public static PolicySelectorRule IncludeAll => new(PolicySelectorAction.Include, SelectorPattern.All, SelectorPattern.All);
}

public sealed record SelectorPattern(PolicyMatchKind Kind, string Value)
{
    public static SelectorPattern All => new(PolicyMatchKind.All, "*");
}

public sealed record PolicyInventory(IReadOnlyList<PolicyInventoryTable> Tables);

public sealed record PolicyInventoryTable(string Database, string Table);

public sealed record PolicyEvaluationRequest(PolicyInventory Inventory);

public sealed record PolicyEvaluationDto(Guid PolicyId, string PolicyName, Guid SourceClusterId, int SelectorJsonVersion, PolicySelector Selector, IReadOnlyList<PolicyInventoryTable> Tables);

public sealed record PolicySimulationRequest(Guid SourceClusterId, PolicySelector Selector);

public sealed record PolicySimulationDto(Guid SourceClusterId, PolicySelector Selector, IReadOnlyList<PolicyInventoryTable> Inventory, IReadOnlyList<PolicyInventoryTable> Tables);

