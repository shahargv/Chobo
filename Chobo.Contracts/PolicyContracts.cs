namespace Chobo.Contracts;

public enum PolicyMatchKind { All, Exact, Wildcard }

public enum PolicySelectorAction { Include, Exclude }

public sealed record BackupPolicyDto(Guid Id, string Name, Guid SourceClusterId, Guid TargetId, int SelectorJsonVersion, PolicySelector Selector, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record UpsertPolicyRequest(string Name, Guid SourceClusterId, Guid TargetId, PolicySelector Selector);

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
