import { act } from "react";
import { createRoot } from "react-dom/client";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import type { BackupDto, BackupPolicyDto, BackupTableDto, BackupTableShardDto } from "../api/generated";
import { ApiContext } from "../api-context";
import { Backups, BackupTablesTable, calculateBackupShardCompletion, calculateBackupSizeBytes, calculateTableSizeBytes, summarizeBackupShards } from "./BackupsPage";

const baseShard = (overrides: Partial<BackupTableShardDto>): BackupTableShardDto => ({
  id: overrides.id ?? crypto.randomUUID(),
  backupTableId: overrides.backupTableId ?? "table-id",
  effectiveBackupType: overrides.effectiveBackupType ?? "Full",
  parentFullBackupId: overrides.parentFullBackupId ?? null,
  parentFullBackupTableShardId: overrides.parentFullBackupTableShardId ?? null,
  sourceShardNumber: overrides.sourceShardNumber ?? 1,
  sourceShardName: overrides.sourceShardName ?? null,
  replicaNumber: overrides.replicaNumber ?? 1,
  host: overrides.host ?? "source-s1",
  port: overrides.port ?? 9000,
  useTls: overrides.useTls ?? false,
  storagePath: overrides.storagePath ?? "s3://backup/table/shard",
  backupSizeBytes: overrides.backupSizeBytes ?? 0,
  status: overrides.status ?? "Queued",
  clickHouseOperationId: overrides.clickHouseOperationId ?? null,
  clickHouseStatus: overrides.clickHouseStatus ?? null,
  startedAt: overrides.startedAt ?? null,
  completedAt: overrides.completedAt ?? "2026-06-21T00:00:00Z",
  error: overrides.error ?? null
});

const baseTable = (overrides: Partial<BackupTableDto> & { shards: BackupTableShardDto[] }): BackupTableDto => ({
  id: overrides.id ?? crypto.randomUUID(),
  backupId: overrides.backupId ?? "backup-id",
  effectiveBackupType: overrides.effectiveBackupType ?? "Full",
  parentFullBackupId: overrides.parentFullBackupId ?? null,
  parentFullBackupTableId: overrides.parentFullBackupTableId ?? null,
  database: overrides.database ?? "sales",
  table: overrides.table ?? "orders",
  engine: overrides.engine ?? "MergeTree",
  dataBackedUp: overrides.dataBackedUp ?? true,
  schemaDefinitionId: overrides.schemaDefinitionId ?? "schema-id",
  storagePath: overrides.storagePath ?? "s3://backup/table",
  backupSizeBytes: overrides.backupSizeBytes ?? 0,
  status: overrides.status ?? "Running",
  clickHouseOperationId: overrides.clickHouseOperationId ?? null,
  clickHouseStatus: overrides.clickHouseStatus ?? null,
  startedAt: overrides.startedAt ?? null,
  completedAt: overrides.completedAt ?? "2026-06-21T00:00:00Z",
  error: overrides.error ?? null,
  shards: overrides.shards
});


describe("backup shard completion", () => {
  it("uses green for all successful shards", () => {
    const table = baseTable({ shards: [baseShard({ status: "Succeeded" }), baseShard({ id: "s2", sourceShardNumber: 2, status: "Succeeded" })] });

    expect(calculateBackupShardCompletion([table])).toEqual({ succeeded: 2, failed: 0, total: 2, percent: 100, tone: "ok" });
  });

  it("uses yellow while shards are still in progress without failures", () => {
    const table = baseTable({ shards: [baseShard({ status: "Succeeded" }), baseShard({ id: "s2", sourceShardNumber: 2, status: "Running" })] });

    expect(calculateBackupShardCompletion([table])).toEqual({ succeeded: 1, failed: 0, total: 2, percent: 50, tone: "warn" });
  });

  it("uses red when any shard failed", () => {
    const table = baseTable({ shards: [baseShard({ status: "Succeeded" }), baseShard({ id: "s2", sourceShardNumber: 2, status: "Failed" })] });

    expect(calculateBackupShardCompletion([table])).toEqual({ succeeded: 1, failed: 1, total: 2, percent: 50, tone: "bad" });
  });
});
describe("BackupTablesTable", () => {
  it("renders each shard as a standalone row with table context", async () => {
    const single = baseTable({
      id: "single-table",
      table: "single_orders",
      backupSizeBytes: 1536,
      shards: [baseShard({ id: "single-shard", sourceShardName: "single", status: "Succeeded", backupSizeBytes: 1536 })]
    });
    const sharded = baseTable({
      id: "wide-table",
      table: "wide_orders",
      backupSizeBytes: 2048,
      shards: [
        baseShard({ id: "queued-1", sourceShardNumber: 1, sourceShardName: "s1", status: "Queued", backupSizeBytes: 0 }),
        baseShard({ id: "queued-2", sourceShardNumber: 2, sourceShardName: "s2", status: "Queued", backupSizeBytes: 0 }),
        baseShard({ id: "running-3", sourceShardNumber: 3, sourceShardName: "s3", status: "Running", backupSizeBytes: 0 }),
        baseShard({ id: "done-4", sourceShardNumber: 4, sourceShardName: "s4", status: "Succeeded", backupSizeBytes: 2048 })
      ]
    });
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => {
      root.render(<BackupTablesTable tableRows={[single, sharded]} isLoading={false} />);
    });

    expect(summarizeBackupShards(sharded.shards)).toEqual({
      shardCount: 4,
      queued: 2,
      running: 1,
      completed: 1,
      succeeded: 1,
      failed: 0,
      skipped: 0
    });
    expect(host.textContent).toContain("sales.single_orders");
    expect(host.textContent).toContain("1.5 KB");
    expect(host.textContent).toContain("sales.wide_orders");
    expect(host.textContent).toContain("Shard 1 (s1), replica 1");
    expect(host.textContent).toContain("Shard 1 (single), replica 1");
    expect(host.querySelectorAll("tbody tr")).toHaveLength(5);
    expect(calculateTableSizeBytes(single)).toBe(1536);
    expect(calculateBackupSizeBytes([single, sharded])).toBe(3584);

    await act(async () => root.unmount());
    host.remove();
  });
  it("opens exact shard failure details from the compact details icon", async () => {
    const table = baseTable({
      table: "failed_orders",
      shards: [baseShard({ id: "failed-shard", status: "Failed", error: "Timeout while connecting to source-s1:9000" })]
    });
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => {
      root.render(<BackupTablesTable tableRows={[table]} isLoading={false} />);
    });

    expect(host.textContent).not.toContain("Timeout while connecting");
    const detailButton = host.querySelector('button[aria-label="Show failure details for sales.failed_orders Shard 1, replica 1"]') as HTMLButtonElement | null;
    expect(detailButton).toBeTruthy();

    await act(async () => {
      detailButton!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    expect(host.textContent).toContain("sales.failed_orders Shard 1, replica 1");
    expect(host.textContent).toContain("Timeout while connecting to source-s1:9000");

    await act(async () => root.unmount());
    host.remove();
  });
});
describe("Backups destructive delete flow", () => {
  it("shows confirmation and sends confirmDestructive for non-pinned backup deletes", async () => {
    const backup = baseBackup({ id: "backup-delete-id", isPinned: false });
    const deleteBackup = vi.fn(async () => ({ ...backup, status: "ManualDeleteRequested" as const }));
    const api = {
      backups: vi.fn(async () => [backup]),
      schedules: vi.fn(async () => []),
      policies: vi.fn(async () => []),
      deleteBackup,
      pinBackup: vi.fn(),
      unpinBackup: vi.fn(),
      cancelBackup: vi.fn()
    };
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

    await act(async () => {
      root.render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={["/backups"]}>
            <ApiContext.Provider value={{ api: api as never, showToast: vi.fn() }}>
              <Backups />
            </ApiContext.Provider>
          </MemoryRouter>
        </QueryClientProvider>
      );
    });
    await flushUi();

    const deleteButton = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "Delete") as HTMLButtonElement | undefined;
    expect(deleteButton).toBeTruthy();
    await act(async () => {
      deleteButton!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    expect(host.textContent).toContain("Delete backup backup-delete-id");
    expect(host.textContent).toContain("This is destructive and cannot be undone.");
    expect(host.textContent).toContain("Any associated child backups will also be deleted.");
    const confirmButton = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "Delete backup") as HTMLButtonElement | undefined;
    expect(confirmButton).toBeTruthy();

    await act(async () => {
      confirmButton!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flushUi();

    expect(deleteBackup).toHaveBeenCalledWith("backup-delete-id", { force: false, confirmDestructive: true });

    await act(async () => root.unmount());
    queryClient.clear();
    host.remove();
  });

  it("deletes multiple selected backups after one confirmation", async () => {
    const first = baseBackup({ id: "bulk-delete-one", isPinned: false });
    const second = baseBackup({ id: "bulk-delete-two", isPinned: true });
    const deleteBackup = vi.fn(async (id: string) => ({ ...baseBackup({ id }), status: "ManualDeleteRequested" as const }));
    const api = {
      backups: vi.fn(async () => [first, second]),
      schedules: vi.fn(async () => []),
      policies: vi.fn(async () => []),
      deleteBackup,
      pinBackup: vi.fn(),
      unpinBackup: vi.fn(),
      cancelBackup: vi.fn()
    };
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

    await act(async () => {
      root.render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={["/backups"]}>
            <ApiContext.Provider value={{ api: api as never, showToast: vi.fn() }}>
              <Backups />
            </ApiContext.Provider>
          </MemoryRouter>
        </QueryClientProvider>
      );
    });
    await flushUi();

    const checkboxes = Array.from(host.querySelectorAll('input[type="checkbox"]')) as HTMLInputElement[];
    expect(checkboxes).toHaveLength(2);
    await act(async () => {
      checkboxes.forEach((checkbox) => checkbox.dispatchEvent(new MouseEvent("click", { bubbles: true })));
    });

    const deleteSelected = Array.from(host.querySelectorAll("button")).find((button) => button.textContent?.includes("Delete selected")) as HTMLButtonElement | undefined;
    expect(deleteSelected).toBeTruthy();
    await act(async () => {
      deleteSelected!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    expect(host.textContent).toContain("Delete 2 backups");
    expect(host.textContent).toContain("Any associated child backups will also be deleted.");
    expect(host.textContent).toContain("1 selected backup is pinned");
    const confirmButton = Array.from(host.querySelectorAll("button")).find((button) => button.textContent?.includes("Force delete backups")) as HTMLButtonElement | undefined;
    expect(confirmButton).toBeTruthy();

    await act(async () => {
      confirmButton!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flushUi();

    expect(deleteBackup).toHaveBeenCalledTimes(2);
    expect(deleteBackup).toHaveBeenCalledWith("bulk-delete-one", { force: false, confirmDestructive: true });
    expect(deleteBackup).toHaveBeenCalledWith("bulk-delete-two", { force: true, confirmDestructive: true });

    await act(async () => root.unmount());
    queryClient.clear();
    host.remove();
  });

  it("refreshes backup table details when the drawer refresh button is clicked", async () => {
    const backup = baseBackup({ id: "backup-detail-id", status: "Running", childBackupIds: ["child-backup-id"] });
    const table = baseTable({ id: "table-detail-id", backupId: backup.id, table: "orders", shards: [baseShard({ id: "shard-detail-id", status: "Queued" })] });
    const backupApi = vi.fn(async (_id: string, options: { includeTables?: boolean } = {}) => options.includeTables ? { ...backup, tables: [table] } : backup);
    const api = {
      backups: vi.fn(async () => [backup]),
      backup: backupApi,
      schedules: vi.fn(async () => []),
      policies: vi.fn(async () => []),
      logs: vi.fn(async () => ({ items: [], offset: 0, limit: 500, totalCount: 0 })),
      audits: vi.fn(async () => ({ items: [], offset: 0, limit: 500, totalCount: 0 })),
      deleteBackup: vi.fn(),
      pinBackup: vi.fn(),
      unpinBackup: vi.fn(),
      cancelBackup: vi.fn()
    };
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } });

    await act(async () => {
      root.render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={["/backups/backup-detail-id"]}>
            <Routes>
              <Route path="/backups/:backupId" element={<ApiContext.Provider value={{ api: api as never, showToast: vi.fn() }}><Backups /></ApiContext.Provider>} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      );
    });
    await flushUi();

    const tableCallsBeforeRefresh = backupApi.mock.calls.filter(([, options]) => options?.includeTables).length;
    expect(tableCallsBeforeRefresh).toBeGreaterThan(0);
    const completionBadge = host.querySelector(".backup-completion");
    expect(completionBadge?.textContent).toBe("0% (0/1 table-shards)");
    expect(completionBadge?.classList.contains("warn")).toBe(true);
    expect(host.textContent).toContain("Child backups");
    expect(host.textContent).toContain("child-backup-id");
    const refreshButton = Array.from(host.querySelectorAll("button")).find((button) => button.textContent?.includes("Refresh")) as HTMLButtonElement | undefined;
    expect(refreshButton).toBeTruthy();

    await act(async () => {
      refreshButton!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flushUi();

    const tableCallsAfterRefresh = backupApi.mock.calls.filter(([, options]) => options?.includeTables).length;
    expect(tableCallsAfterRefresh).toBeGreaterThan(tableCallsBeforeRefresh);

    await act(async () => root.unmount());
    queryClient.clear();
    host.remove();
  });
  it("manual backup starts from inherited ClickHouse settings and submits the edited final dictionary", async () => {
    const policy = basePolicy({ clickHouseBackupSettings: { backup_threads: 4 } });
    const manualBackup = vi.fn(async (request) => baseBackup({ id: "queued-backup", clickHouseBackupSettings: request.clickHouseBackupSettings }));
    const api = {
      backups: vi.fn(async () => []),
      schedules: vi.fn(async () => []),
      policies: vi.fn(async () => [policy]),
      backupSettingsPreview: vi.fn(async () => ({
        settings: { backup_threads: 4, max_backup_bandwidth: 104857600 },
        sources: [
          { name: "max_backup_bandwidth", value: 104857600, source: "cluster" },
          { name: "backup_threads", value: 4, source: "policy" }
        ]
      })),
      manualBackup,
      deleteBackup: vi.fn(),
      pinBackup: vi.fn(),
      unpinBackup: vi.fn(),
      cancelBackup: vi.fn()
    };
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

    await act(async () => {
      root.render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={["/backups"]}>
            <ApiContext.Provider value={{ api: api as never, showToast: vi.fn() }}>
              <Backups />
            </ApiContext.Provider>
          </MemoryRouter>
        </QueryClientProvider>
      );
    });
    await flushUi();

    const openManual = Array.from(host.querySelectorAll("button")).find((button) => button.textContent?.includes("Manual backup")) as HTMLButtonElement;
    await act(async () => openManual.dispatchEvent(new MouseEvent("click", { bubbles: true })));
    await flushUi();
    await flushUi();

    expect(api.backupSettingsPreview).toHaveBeenCalledWith({ clusterId: "source-cluster-id", policyId: "policy-id" });
    expect(host.textContent).toContain("policy, cluster");
    const disclosure = Array.from(host.querySelectorAll('button[aria-expanded="false"]')).find((button) => button.textContent?.includes("ClickHouse backup settings")) as HTMLButtonElement;
    await act(async () => disclosure.dispatchEvent(new MouseEvent("click", { bubbles: true })));

    const removeThreads = host.querySelector('button[aria-label="Remove backup_threads"]') as HTMLButtonElement;
    await act(async () => removeThreads.dispatchEvent(new MouseEvent("click", { bubbles: true })));

    const queueButton = Array.from(host.querySelectorAll("button")).find((button) => button.textContent?.includes("Queue backup")) as HTMLButtonElement;
    await act(async () => queueButton.dispatchEvent(new MouseEvent("click", { bubbles: true })));
    await flushUi();

    expect(manualBackup).toHaveBeenCalledWith(expect.objectContaining({
      policyId: "policy-id",
      clickHouseBackupSettings: { max_backup_bandwidth: 104857600 }
    }));

    await act(async () => root.unmount());
    queryClient.clear();
    host.remove();
  });
});

async function flushUi() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}
function baseBackup(overrides: Partial<BackupDto> = {}): BackupDto {
  return {
    id: overrides.id ?? "backup-id",
    triggerType: overrides.triggerType ?? "Manual",
    status: overrides.status ?? "Succeeded",
    backupType: overrides.backupType ?? "Full",
    contentMode: overrides.contentMode ?? "SchemaAndData",
    clickHouseBackupSettings: overrides.clickHouseBackupSettings ?? {},
    sourceClusterId: overrides.sourceClusterId ?? "source-cluster-id",
    targetId: overrides.targetId ?? "target-id",
    policyId: overrides.policyId ?? null,
    scheduleId: overrides.scheduleId ?? null,
    requestedByUserId: overrides.requestedByUserId ?? null,
    requestedByName: overrides.requestedByName ?? "operator",
    manualRequestJson: overrides.manualRequestJson ?? null,
    createdAt: overrides.createdAt ?? "2026-06-22T00:00:00Z",
    startedAt: overrides.startedAt ?? "2026-06-22T00:01:00Z",
    endedAt: overrides.endedAt ?? "2026-06-22T00:02:00Z",
    error: overrides.error ?? null,
    failureReason: overrides.failureReason ?? null,
    isPinned: overrides.isPinned ?? false,
    pinnedAt: overrides.pinnedAt ?? null,
    pinnedByUserId: overrides.pinnedByUserId ?? null,
    pinnedByName: overrides.pinnedByName ?? null,
    deletionReason: overrides.deletionReason ?? null,
    deletionRequestedAt: overrides.deletionRequestedAt ?? null,
    deletionStartedAt: overrides.deletionStartedAt ?? null,
    deletedAt: overrides.deletedAt ?? null,
    deletionError: overrides.deletionError ?? null,
    deletionAttemptCount: overrides.deletionAttemptCount ?? 0,
    tableCount: overrides.tableCount ?? 1,
    backupSizeBytes: overrides.backupSizeBytes ?? 0,
    relatedFullBackupIds: overrides.relatedFullBackupIds ?? [],
    childBackupIds: overrides.childBackupIds ?? [],
    tables: overrides.tables ?? []
  };
}
function basePolicy(overrides: Partial<BackupPolicyDto> = {}): BackupPolicyDto {
  return {
    id: overrides.id ?? "policy-id",
    name: overrides.name ?? "Nightly",
    sourceClusterId: overrides.sourceClusterId ?? "source-cluster-id",
    targetId: overrides.targetId ?? "target-id",
    contentMode: overrides.contentMode ?? "SchemaAndData",
    selectorJsonVersion: overrides.selectorJsonVersion ?? 1,
    selector: overrides.selector ?? { version: 1, rules: [{ action: "Include", database: { kind: "All", value: "*" }, table: { kind: "All", value: "*" } }] },
    retention: overrides.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 },
    failedBackupRetentionMode: overrides.failedBackupRetentionMode ?? "KeepAndExcludeFromMinBackupsToKeep",
    clickHouseBackupSettings: overrides.clickHouseBackupSettings ?? {},
    clickHouseRestoreSettings: overrides.clickHouseRestoreSettings ?? {},
    maxAgeHoursForBaseBackup: overrides.maxAgeHoursForBaseBackup ?? null,
    effectiveMaxAgeHoursForBaseBackup: overrides.effectiveMaxAgeHoursForBaseBackup ?? 168,
    isSystemDefault: overrides.isSystemDefault ?? false,
    isDeleted: overrides.isDeleted ?? false,
    createdAt: overrides.createdAt ?? "2026-06-22T00:00:00Z",
    updatedAt: overrides.updatedAt ?? null
  };
}
