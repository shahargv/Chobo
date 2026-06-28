import { act } from "react";
import { createRoot } from "react-dom/client";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import type { BackupDto, DashboardDto } from "../api/generated";
import { ApiContext } from "../api-context";
import { Dashboard } from "./DashboardPage";

describe("Dashboard backup detail links", () => {
  it("opens the backup detail drawer from the latest backups panel", async () => {
    const backup = baseBackup({ id: "latest-backup-id" });
    const backupApi = vi.fn(async (id: string, options: { includeTables?: boolean } = {}) => ({
      ...backup,
      id,
      tables: options.includeTables ? [] : backup.tables
    }));
    const api = {
      dashboard: vi.fn(async () => baseDashboard()),
      clusters: vi.fn(async () => []),
      targets: vi.fn(async () => []),
      policies: vi.fn(async () => []),
      schedules: vi.fn(async () => []),
      backups: vi.fn(async () => [backup]),
      backup: backupApi,
      restores: vi.fn(async () => []),
      logs: vi.fn(async () => ({ items: [], offset: 0, limit: 500, totalCount: 0 })),
      audits: vi.fn(async () => ({ items: [], offset: 0, limit: 500, totalCount: 0 })),
      cancelBackup: vi.fn(),
      moveQueueOperation: vi.fn()
    };
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } });

    await act(async () => {
      root.render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={["/"]}>
            <ApiContext.Provider value={{ api: api as never, showToast: vi.fn() }}>
              <Dashboard />
            </ApiContext.Provider>
          </MemoryRouter>
        </QueryClientProvider>
      );
    });
    await flushUi();

    const latestBackupButton = Array.from(host.querySelectorAll("button.link-button.mono"))
      .find((button) => button.textContent === "latest-backup-id") as HTMLButtonElement | undefined;
    expect(latestBackupButton).toBeTruthy();

    await act(async () => {
      latestBackupButton!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flushUi();

    expect(host.textContent).toContain("Backup detail");
    expect(backupApi).toHaveBeenCalledWith("latest-backup-id", { includeTables: false });

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

function baseDashboard(): DashboardDto {
  return {
    generatedAt: "2026-06-22T00:00:00Z",
    futureWindowHours: 6,
    queue: { activeCount: 0, oldestActiveQueuedAt: "", oldestActiveAgeSeconds: 0 },
    runningBackups: [],
    schedules: [],
    futureSchedules: []
  };
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
    tables: overrides.tables ?? []
  };
}