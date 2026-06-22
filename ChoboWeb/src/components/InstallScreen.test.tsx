import { afterEach, describe, expect, it, vi } from "vitest";
import { copyText } from "./InstallScreen";

describe("copyText", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    Reflect.deleteProperty(navigator, "clipboard");
    Reflect.deleteProperty(document, "execCommand");
  });

  it("uses navigator clipboard when available", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });

    await expect(copyText("initial-token")).resolves.toBe(true);
    expect(writeText).toHaveBeenCalledWith("initial-token");
  });

  it("falls back to execCommand copy when navigator clipboard is unavailable", async () => {
    const execCommand = vi.fn().mockReturnValue(true);
    Object.defineProperty(document, "execCommand", { configurable: true, value: execCommand });

    await expect(copyText("initial-token")).resolves.toBe(true);
    expect(execCommand).toHaveBeenCalledWith("copy");
  });
});
