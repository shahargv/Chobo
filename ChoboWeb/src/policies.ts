import type { PolicyInventoryTable, PolicySelector, PolicySelectorRule } from "./api/generated";

export const emptySelector: PolicySelector = {
  version: 1,
  rules: [
    {
      action: "Include",
      database: { kind: "All", value: "*" },
      table: { kind: "All", value: "*" }
    }
  ]
};

export function evaluateSelector(selector: PolicySelector, inventory: PolicyInventoryTable[]) {
  return inventory.filter((table) => {
    let included = false;
    for (const rule of selector.rules) {
      if (matchesRule(rule, table)) included = rule.action === "Include";
    }
    return included;
  });
}

export function matchesRule(rule: PolicySelectorRule, table: PolicyInventoryTable) {
  return matchesPattern(rule.database.kind, rule.database.value, table.database) &&
    matchesPattern(rule.table.kind, rule.table.value, table.table);
}

function matchesPattern(kind: string, value: string, candidate: string) {
  if (kind === "All") return true;
  if (kind === "Exact") return candidate === value;
  const escaped = value.split("*").map(escapeRegex).join(".*");
  return new RegExp(`^${escaped}$`).test(candidate);
}

function escapeRegex(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
