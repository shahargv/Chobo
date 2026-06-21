import { act } from "react";
import { createRoot } from "react-dom/client";
import { describe, expect, it } from "vitest";
import { SelectedTablesPreview } from "./PoliciesPage";

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
});
