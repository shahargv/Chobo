import { act } from "react";
import { createRoot } from "react-dom/client";
import { describe, expect, it, vi } from "vitest";
import { backupTypeForPolicyRun, copyRule, excludeTableRule, SelectedTablesPreview } from "./PoliciesPage";

describe("SelectedTablesPreview", () => {
  it("caps 1,000 selected table chips by default and can show all", async () => {
    const selected = Array.from({ length: 1000 }, (_, index) => ({
      database: "large_schema",
      table: `table_${index.toString().padStart(4, "0")}`
    }));
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => {
      root.render(<SelectedTablesPreview inventoryCount={1000} selected={selected} />);
    });

    expect(host.querySelectorAll(".chip")).toHaveLength(100);
    expect(host.textContent).toContain("1000 of 1000 table(s) will be backed up.");
    expect(host.textContent).toContain("900 more matched table(s) are included.");
    const showAll = host.querySelector<HTMLButtonElement>("button.link-button");
    expect(showAll?.textContent).toBe("Show all");

    await act(async () => {
      showAll!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    expect(host.querySelectorAll(".chip")).toHaveLength(1000);
    expect(host.textContent).toContain("large_schema.table_0999");

    await act(async () => root.unmount());
    host.remove();
  });

  it("calls back with the table when the chip exclude button is clicked", async () => {
    const selected = [{ database: "analytics", table: "events" }];
    const onExcludeTable = vi.fn();
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => {
      root.render(<SelectedTablesPreview inventoryCount={1} selected={selected} onExcludeTable={onExcludeTable} />);
    });

    const exclude = host.querySelector<HTMLButtonElement>('button[aria-label="Exclude analytics.events"]');
    expect(exclude).not.toBeNull();

    await act(async () => {
      exclude!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    expect(onExcludeTable).toHaveBeenCalledWith({ database: "analytics", table: "events" });

    await act(async () => root.unmount());
    host.remove();
  });
});

describe("policy selector rules", () => {
  it("builds exact exclude rules for selected tables", () => {
    expect(excludeTableRule({ database: "analytics", table: "events" })).toEqual({
      action: "Exclude",
      database: { kind: "Exact", value: "analytics" },
      table: { kind: "Exact", value: "events" }
    });
  });

  it("duplicates selector rules without sharing pattern references", () => {
    const rule = { action: "Include" as const, database: { kind: "Exact" as const, value: "db" }, table: { kind: "Wildcard" as const, value: "events_*" } };
    const copy = copyRule(rule);

    expect(copy).toEqual(rule);
    expect(copy).not.toBe(rule);
    expect(copy.database).not.toBe(rule.database);
    expect(copy.table).not.toBe(rule.table);
  });
});
describe("policy run options", () => {
  it("maps explicit run modes to the backup type sent to the API", () => {
    expect(backupTypeForPolicyRun({ contentMode: "SchemaAndData" }, "regular")).toBe("Incremental");
    expect(backupTypeForPolicyRun({ contentMode: "SchemaAndData" }, "full")).toBe("Full");
    expect(backupTypeForPolicyRun({ contentMode: "SchemaOnly" }, "regular")).toBe("Full");
  });
});
