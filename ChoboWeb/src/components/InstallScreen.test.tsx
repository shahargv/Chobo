import { afterEach, describe, expect, it, vi } from "vitest";
import { copyTextToClipboard } from "./InstallScreen";

const originalClipboard = navigator.clipboard;
const originalExecCommand = document.execCommand;
const originalIsSecureContext = window.isSecureContext;

afterEach(() => {
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: originalClipboard });
  Object.defineProperty(window, "isSecureContext", { configurable: true, value: originalIsSecureContext });
  document.execCommand = originalExecCommand;
  vi.restoreAllMocks();
});

describe("copyTextToClipboard", () => {
  it("copies with the Clipboard API in secure browser contexts", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    const execCommand = vi.fn();
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    Object.defineProperty(window, "isSecureContext", { configurable: true, value: true });
    document.execCommand = execCommand;

    await copyTextToClipboard("initial-token");

    expect(writeText).toHaveBeenCalledWith("initial-token");
    expect(execCommand).not.toHaveBeenCalled();
  });

  it("falls back to a selected textarea when Clipboard API writes fail", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("Denied"));
    const execCommand = vi.fn().mockReturnValue(true);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    Object.defineProperty(window, "isSecureContext", { configurable: true, value: true });
    document.execCommand = execCommand;

    await copyTextToClipboard("fallback-token");

    expect(writeText).toHaveBeenCalledWith("fallback-token");
    expect(execCommand).toHaveBeenCalledWith("copy");
    expect(document.querySelector("textarea")).toBeNull();
  });

  it("reports failure when no copy strategy succeeds", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("Denied"));
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    Object.defineProperty(window, "isSecureContext", { configurable: true, value: true });
    document.execCommand = vi.fn().mockReturnValue(false);

    await expect(copyTextToClipboard("uncopied-token")).rejects.toThrow("Clipboard copy failed.");
  });
});
