import { act } from "react";
import { createRoot } from "react-dom/client";
import { describe, expect, it, vi } from "vitest";
import { ClickHouseAdvancedSettingsEditor, type ClickHouseSettings } from "./ClickHouseAdvancedSettingsEditor";

function renderEditor(value: ClickHouseSettings, onChange = vi.fn(), defaultOpen = true, onValidityChange = vi.fn()) {
  const host = document.createElement("div");
  document.body.appendChild(host);
  const root = createRoot(host);
  act(() => {
    root.render(<ClickHouseAdvancedSettingsEditor title="Advanced" value={value} sources={[{ name: "backup_threads", value: 4, source: "policy" }]} onChange={onChange} defaultOpen={defaultOpen} onValidityChange={onValidityChange} />);
  });
  return { host, root, onChange, onValidityChange };
}

describe("ClickHouseAdvancedSettingsEditor", () => {
  it("is collapsed by default and expands from the disclosure", () => {
    const { host, root } = renderEditor({ backup_threads: 4 }, vi.fn(), false);

    expect(host.querySelector('input[value="backup_threads"]')).toBeNull();
    const disclosure = host.querySelector('button[aria-expanded="false"]') as HTMLButtonElement;
    act(() => disclosure.dispatchEvent(new MouseEvent("click", { bubbles: true })));

    expect((host.querySelector(".settings-row input") as HTMLInputElement).value).toBe("backup_threads");
    act(() => root.unmount());
    host.remove();
  });

  it("keeps a blank draft row visible until it is filled", () => {
    const onChange = vi.fn();
    const onValidityChange = vi.fn();
    const { host, root } = renderEditor({}, onChange, false, onValidityChange);

    const add = Array.from(host.querySelectorAll("button")).find((button) => button.textContent?.includes("Add setting")) as HTMLButtonElement;
    act(() => add.dispatchEvent(new MouseEvent("click", { bubbles: true })));

    expect(host.textContent).toContain("Setting name is required.");
    expect(host.querySelectorAll(".settings-row")).toHaveLength(1);
    expect(onValidityChange).toHaveBeenLastCalledWith(false);
    expect(onChange).not.toHaveBeenCalled();

    act(() => root.unmount());
    host.remove();
  });

  it("shows inherited source labels and removes settings from the final dictionary", () => {
    const onChange = vi.fn();
    const { host, root } = renderEditor({ backup_threads: 4, max_backup_bandwidth: 1024 }, onChange);

    expect(host.textContent).toContain("policy");
    expect(host.textContent).toContain("operation");

    const removeBackupThreads = host.querySelector('button[aria-label="Remove backup_threads"]') as HTMLButtonElement;
    act(() => removeBackupThreads.dispatchEvent(new MouseEvent("click", { bubbles: true })));

    expect(onChange).toHaveBeenCalledWith({ max_backup_bandwidth: 1024 });
    act(() => root.unmount());
    host.remove();
  });

  it("validates reserved keys, duplicate names, invalid names, and numeric values", () => {
    const { host, root } = renderEditor({ base_backup: "x", Backup_Threads: 1, backup_threads: 2, "bad-name": 3, max_backup_bandwidth: Number.NaN });

    expect(host.textContent).toContain("base_backup is managed by Chobo and cannot be set.");
    expect(host.textContent).toContain("Backup_Threads is duplicated.");
    expect(host.textContent).toContain("bad-name is not a valid ClickHouse setting name.");
    expect(host.textContent).toContain("max_backup_bandwidth requires a numeric value.");

    act(() => root.unmount());
    host.remove();
  });
});
