import { describe, expect, it } from "vitest";
import { evaluateSelector } from "./policies";

describe("policy selector evaluation preview", () => {
  it("applies ordered include and exclude rules like the server contract", () => {
    const tables = [
      { database: "sales", table: "orders" },
      { database: "system", table: "query_log" },
      { database: "tenant_demo", table: "daily_scratch" },
      { database: "tenant_gold", table: "monthly_scratch" }
    ];

    const selected = evaluateSelector({
      version: 1,
      rules: [
        { action: "Include", database: { kind: "All", value: "*" }, table: { kind: "All", value: "*" } },
        { action: "Exclude", database: { kind: "Exact", value: "system" }, table: { kind: "All", value: "*" } },
        { action: "Exclude", database: { kind: "Wildcard", value: "tenant_*" }, table: { kind: "Wildcard", value: "*_scratch" } },
        { action: "Include", database: { kind: "Exact", value: "tenant_gold" }, table: { kind: "Exact", value: "monthly_scratch" } }
      ]
    }, tables);

    expect(selected).toEqual([
      { database: "sales", table: "orders" },
      { database: "tenant_gold", table: "monthly_scratch" }
    ]);
  });
});
