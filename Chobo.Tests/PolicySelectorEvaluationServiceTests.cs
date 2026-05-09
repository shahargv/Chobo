using Chobo.Contracts;
using ChoboServer.Application;

namespace Chobo.Tests;

public sealed class PolicySelectorEvaluationServiceTests
{
    private readonly PolicySelectorEvaluationService _service = new();

    [Fact]
    public void Empty_selector_includes_every_inventory_table()
    {
        var inventory = Inventory(
            ("sales", "orders"),
            ("sales", "customers"),
            ("system", "query_log"));

        var result = _service.Evaluate(PolicySelector.Empty, inventory);

        Assert.Equal(inventory.Tables, result);
    }

    [Fact]
    public void Later_rules_override_earlier_rules()
    {
        var selector = Selector(
            Include(All(), All()),
            Exclude(Exact("system"), All()),
            Exclude(Wildcard("tenant_*"), Wildcard("*_scratch")),
            Include(Exact("tenant_gold"), Exact("monthly_scratch")));
        var inventory = Inventory(
            ("sales", "orders"),
            ("system", "query_log"),
            ("tenant_gold", "events"),
            ("tenant_gold", "daily_scratch"),
            ("tenant_gold", "monthly_scratch"),
            ("tenant_demo", "daily_scratch"));

        var result = _service.Evaluate(selector, inventory);

        Assert.Equal(
            [
                new PolicyInventoryTable("sales", "orders"),
                new PolicyInventoryTable("tenant_gold", "events"),
                new PolicyInventoryTable("tenant_gold", "monthly_scratch")
            ],
            result);
    }

    [Fact]
    public void Tables_are_excluded_by_default_until_a_rule_includes_them()
    {
        var selector = Selector(
            Include(Exact("sales"), Exact("orders")),
            Include(Wildcard("tenant_*"), Wildcard("fact_*")),
            Exclude(Exact("tenant_demo"), All()));
        var inventory = Inventory(
            ("sales", "orders"),
            ("sales", "customers"),
            ("tenant_a", "fact_usage"),
            ("tenant_a", "dim_user"),
            ("tenant_demo", "fact_usage"));

        var result = _service.Evaluate(selector, inventory);

        Assert.Equal(
            [
                new PolicyInventoryTable("sales", "orders"),
                new PolicyInventoryTable("tenant_a", "fact_usage")
            ],
            result);
    }

    [Fact]
    public void Unsupported_selector_version_is_rejected()
    {
        var selector = new PolicySelector(2, [PolicySelectorRule.IncludeAll]);

        var ex = Assert.Throws<ArgumentException>(() => _service.Evaluate(selector, Inventory(("sales", "orders"))));

        Assert.Contains("version 2", ex.Message);
    }

    [Fact]
    public void Selector_json_version_must_match_payload_version()
    {
        var ex = Assert.Throws<ArgumentException>(() => _service.Evaluate(2, PolicySelector.Empty, Inventory(("sales", "orders"))));

        Assert.Contains("does not match", ex.Message);
    }

    private static PolicySelector Selector(params PolicySelectorRule[] rules) =>
        new(1, rules);

    private static PolicySelectorRule Include(SelectorPattern database, SelectorPattern table) =>
        new(PolicySelectorAction.Include, database, table);

    private static PolicySelectorRule Exclude(SelectorPattern database, SelectorPattern table) =>
        new(PolicySelectorAction.Exclude, database, table);

    private static SelectorPattern All() => SelectorPattern.All;

    private static SelectorPattern Exact(string value) => new(PolicyMatchKind.Exact, value);

    private static SelectorPattern Wildcard(string value) => new(PolicyMatchKind.Wildcard, value);

    private static PolicyInventory Inventory(params (string Database, string Table)[] tables) =>
        new(tables.Select(x => new PolicyInventoryTable(x.Database, x.Table)).ToList());
}
