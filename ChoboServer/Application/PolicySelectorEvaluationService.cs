using System.Text.RegularExpressions;
using Chobo.Contracts;

namespace ChoboServer.Application;

public sealed class PolicySelectorEvaluationService
{
    public IReadOnlyList<PolicyInventoryTable> Evaluate(PolicySelector selector, PolicyInventory inventory)
    {
        return Evaluate(selector.Version, selector, inventory);
    }

    public IReadOnlyList<PolicyInventoryTable> Evaluate(int selectorJsonVersion, PolicySelector selector, PolicyInventory inventory)
    {
        if (selectorJsonVersion != selector.Version)
        {
            throw new ArgumentException("Selector JSON version does not match the selector payload version.", nameof(selectorJsonVersion));
        }

        return selectorJsonVersion switch
        {
            1 => EvaluateV1(selector, inventory),
            _ => throw new ArgumentException($"Selector JSON version {selectorJsonVersion} is not supported.", nameof(selectorJsonVersion))
        };
    }

    private static IReadOnlyList<PolicyInventoryTable> EvaluateV1(PolicySelector selector, PolicyInventory inventory)
    {
        if (selector.Version != 1)
        {
            throw new ArgumentException("Only selector version 1 is supported.", nameof(selector));
        }

        return inventory.Tables
            .Where(table => IsIncluded(selector, table))
            .ToList();
    }

    private static bool IsIncluded(PolicySelector selector, PolicyInventoryTable table)
    {
        var included = false;
        foreach (var rule in selector.Rules)
        {
            if (Matches(rule.Database, table.Database) && Matches(rule.Table, table.Table))
            {
                included = rule.Action == PolicySelectorAction.Include;
            }
        }

        return included;
    }

    private static bool Matches(SelectorPattern pattern, string value) =>
        pattern.Kind switch
        {
            PolicyMatchKind.All => true,
            PolicyMatchKind.Exact => string.Equals(pattern.Value, value, StringComparison.Ordinal),
            PolicyMatchKind.Wildcard => Regex.IsMatch(value, WildcardToRegex(pattern.Value), RegexOptions.CultureInvariant),
            _ => false
        };

    private static string WildcardToRegex(string value) =>
        "\\A" + Regex.Escape(value).Replace("\\*", ".*").Replace("\\?", ".") + "\\z";
}
