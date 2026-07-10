import { act } from "react";
import { createRoot } from "react-dom/client";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import type { BackupDto } from "../../api/generated";
import { ApiContext } from "../../api-context";
import { RestoreWizard } from "./RestoreWizard";

const summaryBackup = (overrides: Partial<BackupDto> = {}): BackupDto => ({
  id: overrides.id ?? "backup-id",
  triggerType: "Scheduled",
  status: "Succeeded",
  backupType: "Full",
  contentMode: "SchemaAndData",
  sourceClusterId: "source-cluster",
  targetId: "target-id",
  policyId: "policy-id",
  scheduleId: null,
  requestedByUserId: null,
  requestedByName: "system",
  manualRequestJson: null,
  storageRootPath: null,
  createdAt: "2026-07-10T00:00:00Z",
  startedAt: "2026-07-01T00:01:00Z",
  endedAt: "2026-07-01T00:02:00Z",
  error: null,
  failureReason: null,
  isPinned: false,
  pinnedAt: null,
  pinnedByUserId: null,
  pinnedByName: null,
  deletionReason: null,
  deletionRequestedAt: null,
  deletionStartedAt: null,
  deletedAt: null,
  deletionError: null,
  deletionAttemptCount: 0,
  tableCount: overrides.tableCount ?? 1,
  backupSizeBytes: 0,
  clickHouseBackupSettings: {},
  relatedFullBackupIds: [],
  childBackupIds: [],
  tables: overrides.tables ?? [],
  encryptionState: overrides.encryptionState ?? "Unencrypted",
  compressionMethod: overrides.compressionMethod ?? null,
  compressionLevel: overrides.compressionLevel ?? null
});

const detailedBackup = summaryBackup({
  tables: [{
    id: "table-id",
    backupId: "backup-id",
    effectiveBackupType: "Full",
    parentFullBackupId: null,
    parentFullBackupTableId: null,
    database: "sales",
    table: "orders",
    engine: "MergeTree",
    dataBackedUp: true,
    schemaDefinitionId: "schema-id",
    storagePath: "s3://bucket/sales/orders",
    backupSizeBytes: 0,
    status: "Succeeded",
    clickHouseOperationId: null,
    clickHouseStatus: null,
    startedAt: "2026-07-01T00:01:00Z",
    completedAt: "2026-07-01T00:02:00Z",
    error: null,
    shards: [{
      id: "shard-id",
      backupTableId: "table-id",
      effectiveBackupType: "Full",
      parentFullBackupId: null,
      parentFullBackupTableShardId: null,
      sourceShardNumber: 1,
      sourceShardName: "s1",
      replicaNumber: 1,
      host: "source-host",
      port: 9000,
      useTls: false,
      storagePath: "s3://bucket/sales/orders/s1",
      backupSizeBytes: 0,
      status: "Succeeded",
      clickHouseOperationId: null,
      clickHouseStatus: null,
      startedAt: "2026-07-01T00:01:00Z",
      completedAt: "2026-07-01T00:02:00Z",
      error: null,
      isPasswordProtected: false,
      passwordKeyId: null,
      passwordKeyAvailable: null
    }]
  }]
});

describe("RestoreWizard backup loading", () => {
  it("loads backup summaries first and fetches selected backup table details lazily", async () => {
    const api = {
      backups: vi.fn(async () => [summaryBackup()]),
      backup: vi.fn(async () => detailedBackup),
      clusters: vi.fn(async () => []),
      policies: vi.fn(async () => []),
      backupSchema: vi.fn(async () => ({ backupId: "backup-id", databases: [] })),
      restoreSettingsPreview: vi.fn(async () => ({ settings: {}, sources: [] })),
      clusterTopology: vi.fn(async () => ({ clusterId: "target", shards: [] })),
      restorePlan: vi.fn(),
      initiateRestore: vi.fn(),
      initiateRestoreFromPlan: vi.fn()
    };
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } });
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => {
      root.render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={["/restores/start"]}>
            <ApiContext.Provider value={{ api: api as never, showToast: vi.fn() }}>
              <RestoreWizard />
            </ApiContext.Provider>
          </MemoryRouter>
        </QueryClientProvider>
      );
    });

    await waitFor(() => host.querySelector('input[aria-label="Use backup backup-id"]'));
    expect(api.backups).toHaveBeenCalledWith({}, { includeTables: false });
    expect(api.backup).not.toHaveBeenCalled();

    await act(async () => {
      (host.querySelector('input[aria-label="Use backup backup-id"]') as HTMLInputElement).click();
    });

    await waitFor(() => api.backup.mock.calls.length > 0);
    expect(api.backup).toHaveBeenCalledWith("backup-id", { includeTables: true });

    root.unmount();
    host.remove();
    queryClient.clear();
  });
});

async function waitFor<T>(callback: () => T | false | null | undefined, timeoutMs = 3000): Promise<T> {
  const started = Date.now();
  let result = callback();
  while (!result) {
    if (Date.now() - started > timeoutMs) throw new Error("Timed out waiting for condition.");
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 20)); });
    result = callback();
  }
  return result;
}
