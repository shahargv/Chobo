import { act } from "react";
import { createRoot } from "react-dom/client";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import { ApiContext } from "../api-context";
import { backupTypeForPolicyRun, copyRule, excludeTableRule, formatPolicyRetentionSummary, Policies, SelectedTablesPreview } from "./PoliciesPage";

describe("optional policy protection", () => {
  it("defaults password protection and compression to disabled and reveals each independently", async () => {
    const api = { policies: vi.fn(async () => []), clusters: vi.fn(async () => []), targets: vi.fn(async () => []) };
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => root.render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/policies"]}>
          <Routes><Route path="/policies" element={<ApiContext.Provider value={{ api: api as never, showToast: vi.fn() }}><Policies /></ApiContext.Provider>} /></Routes>
        </MemoryRouter>
      </QueryClientProvider>
    ));
    await act(async () => { await Promise.resolve(); });
    const add = Array.from(host.querySelectorAll("button")).find(button => button.textContent?.includes("Add policy"));
    await act(async () => add!.dispatchEvent(new MouseEvent("click", { bubbles: true })));

    expect(host.textContent).toContain("Password protection (optional)");
    expect(host.textContent).toContain("Compression (optional)");
    expect(host.textContent).toContain("Leave disabled for normal unencrypted backups");
    expect(host.textContent).toContain("Leave disabled to preserve the normal backup format");
    const selects = Array.from(host.querySelectorAll("select"));
    const password = selects.find(select => select.parentElement?.textContent?.includes("Password protection (optional)"));
    const compression = selects.find(select => select.parentElement?.textContent?.includes("Compression (optional)"));
    expect(password?.value).toBe("None");
    expect(compression?.value).toBe("");
    expect(host.querySelector('input[type="password"]')).toBeNull();

    await act(async () => {
      password!.value = "Constant";
      password!.dispatchEvent(new Event("change", { bubbles: true }));
      compression!.value = "Lzma";
      compression!.dispatchEvent(new Event("change", { bubbles: true }));
    });
    expect(host.querySelector('input[type="password"]')).not.toBeNull();
    expect(host.textContent).toContain("Compression level (optional)");

    await act(async () => root.unmount());
    queryClient.clear();
    host.remove();
  });
});

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

describe("policy retention summary", () => {
  it("formats limited retention counts, times, and explicit full backup age", () => {
    expect(formatPolicyRetentionSummary({
      retention: { fullRetentionMinutes: 1440, incrementalRetentionMinutes: 720, minBackupsToKeep: 8, minFullBackupsToKeep: 3 },
      maxAgeHoursForBaseBackup: 24
    })).toEqual([
      "Min backups to keep: full - 3, any - 8",
      "Retention time: full - 1440 minutes, any - 720 minutes",
      "New full backup every 24 hours"
    ]);
  });

  it("formats missing or zero retention limits as unlimited", () => {
    expect(formatPolicyRetentionSummary({
      retention: { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 },
      maxAgeHoursForBaseBackup: null
    })).toEqual([
      "Min backups to keep: full - unlimited, any - unlimited",
      "Retention time: full - unlimited, any - unlimited"
    ]);
  });
});
describe("policy run options", () => {
  it("maps explicit run modes to the backup type sent to the API", () => {
    expect(backupTypeForPolicyRun({ contentMode: "SchemaAndData" }, "regular")).toBe("Incremental");
    expect(backupTypeForPolicyRun({ contentMode: "SchemaAndData" }, "full")).toBe("Full");
    expect(backupTypeForPolicyRun({ contentMode: "SchemaOnly" }, "regular")).toBe("Full");
  });
});

