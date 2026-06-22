import { describe, expect, it } from "vitest";
import { formatNodes, parseNodes } from "./ClustersPage";

describe("cluster access node helpers", () => {
  it("formats saved access nodes for editing", () => {
    expect(formatNodes([
      { host: "node-a", port: 9000, useTls: false },
      { host: "node-b", port: 9440, useTls: true }
    ])).toBe("node-a:9000, node-b:9440");
  });

  it("parses multiple comma-separated nodes", () => {
    expect(parseNodes("node-a:9000, node-b:9440", true)).toEqual([
      { host: "node-a", port: 9000, useTls: true },
      { host: "node-b", port: 9440, useTls: true }
    ]);
  });

  it("keeps a node usable while the port is temporarily deleted", () => {
    expect(parseNodes("node-a:", false)).toEqual([{ host: "node-a", port: 9000, useTls: false }]);
  });
});
