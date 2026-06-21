import { act } from "react";
import { createRoot } from "react-dom/client";
import { describe, expect, it } from "vitest";
import type { BackupTableDto, BackupTableShardDto } from "../api/generated";
import { BackupTablesTable, summarizeBackupShards } from "./BackupsPage";

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
  s3Path: overrides.s3Path ?? "s3://backup/table/shard",
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
  s3Path: overrides.s3Path ?? "s3://backup/table",
  status: overrides.status ?? "Running",
  clickHouseOperationId: overrides.clickHouseOperationId ?? null,
  clickHouseStatus: overrides.clickHouseStatus ?? null,
  startedAt: overrides.startedAt ?? null,
  completedAt: overrides.completedAt ?? "2026-06-21T00:00:00Z",
  error: overrides.error ?? null,
  shards: overrides.shards
});

describe("BackupTablesTable", () => {
  it("aggregates shard status and only expands multi-shard tables", async () => {
    const single = baseTable({
      id: "single-table",
      table: "single_orders",
      shards: [baseShard({ id: "single-shard", status: "Succeeded" })]
    });
    const sharded = baseTable({
      id: "wide-table",
      table: "wide_orders",
      shards: [
        baseShard({ id: "queued-1", sourceShardNumber: 1, sourceShardName: "s1", status: "Queued" }),
        baseShard({ id: "queued-2", sourceShardNumber: 2, sourceShardName: "s2", status: "Queued" }),
        baseShard({ id: "running-3", sourceShardNumber: 3, sourceShardName: "s3", status: "Running" }),
        baseShard({ id: "done-4", sourceShardNumber: 4, sourceShardName: "s4", status: "Succeeded" })
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
    expect(host.textContent).toContain("1 shard completed");
    expect(host.textContent).toContain("sales.wide_orders");
    expect(host.textContent).toContain("4 shards: 2 queued, 1 running, 1 completed");
    expect(host.textContent).toContain("Shard 1 (s1)");
    expect(host.textContent).not.toContain("Shard 1 (single)");

    await act(async () => root.unmount());
    host.remove();
  });
});