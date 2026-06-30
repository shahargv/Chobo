import { afterEach, describe, expect, it, vi } from "vitest";
import { ChoboApiClient } from "./client";
import type { InitiateRestoreRequest } from "./generated";

function apiClient() {
  return new ChoboApiClient(() => "token", () => undefined, "http://chobo.test");
}

function stubJsonResponse(body: unknown = {}) {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" }
  }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("ChoboApiClient destructive requests", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("sends destructive confirmation and optional force for backup deletes", async () => {
    const fetchMock = stubJsonResponse({ id: "backup-id" });

    await apiClient().deleteBackup("backup-id", { confirmDestructive: true });
    await apiClient().deleteBackup("pinned-backup-id", { force: true, confirmDestructive: true });

    expect(fetchMock).toHaveBeenNthCalledWith(1, "http://chobo.test/api/v1/backups/backup-id?confirmDestructive=true", expect.objectContaining({ method: "DELETE" }));
    expect(fetchMock).toHaveBeenNthCalledWith(2, "http://chobo.test/api/v1/backups/pinned-backup-id?force=true&confirmDestructive=true", expect.objectContaining({ method: "DELETE" }));
  });

  it("sends destructive confirmation in restore initiation body", async () => {
    const fetchMock = stubJsonResponse({ id: "restore-id" });
    const request: InitiateRestoreRequest = {
      backupId: "backup-id",
      targetClusterId: "cluster-id",
      append: false,
      allowSchemaMismatch: false,
      layout: "Preserve",
      schemaOnly: false,
      confirmDestructive: true,
      clickHouseRestoreSettings: {},
      tables: [{ backupTableId: "table-id", targetDatabase: "sales", targetTable: "orders", append: false, allowSchemaMismatch: false, schemaOnly: false, shardSources: [] }]
    };

    await apiClient().initiateRestore(request);

    const calls = fetchMock.mock.calls as unknown as Array<[string, RequestInit]>;
    const [url, init] = calls[0];
    expect(url).toBe("http://chobo.test/api/v1/restores/initiate");
    expect(init).toEqual(expect.objectContaining({ method: "POST" }));
    expect(JSON.parse(String(init.body))).toMatchObject({ confirmDestructive: true, tables: [{ targetTable: "orders" }] });
  });
});
