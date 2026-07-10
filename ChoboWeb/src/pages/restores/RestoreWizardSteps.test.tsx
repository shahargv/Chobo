import { act } from "react";
import { createRoot } from "react-dom/client";
import { describe, expect, it, vi } from "vitest";
import type { BackupDto, EntityRestorePlanDto, RestoreTableMappingRequest, SchemaTableDto } from "../../api/generated";
import { BackupChoiceStep, ReviewStep, ScopeStep } from "./RestoreWizardSteps";
import type { RestoreMappingDraft } from "./restoreTypes";

const backup: BackupDto = {
  id: "backup-anchor",
  triggerType: "Scheduled",
  status: "Succeeded",
  backupType: "Full",
  contentMode: "SchemaAndData",
  clickHouseBackupSettings: {},
  sourceClusterId: "source-cluster",
  targetId: "backup-target",
  policyId: "policy-nightly",
  scheduleId: null,
  requestedByUserId: null,
  requestedByName: "system",
  manualRequestJson: null,
  storageRootPath: null,
  createdAt: "2026-06-22T00:00:00Z",
  startedAt: "2026-06-22T00:01:00Z",
  endedAt: "2026-06-22T00:02:00Z",
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
  tableCount: 1,
  backupSizeBytes: 0,
  relatedFullBackupIds: [],
  childBackupIds: [],
  encryptionState: "Unencrypted",
  compressionMethod: null,
  compressionLevel: null,
  tables: [{
    id: "table-orders",
    backupId: "backup-anchor",
    effectiveBackupType: "Full",
    parentFullBackupId: null,
    parentFullBackupTableId: null,
    database: "sales",
    table: "orders",
    engine: "MergeTree",
    dataBackedUp: true,
    schemaDefinitionId: "schema-orders",
    storagePath: "s3://bucket/full/orders",
    backupSizeBytes: 0,
    status: "Succeeded",
    clickHouseOperationId: null,
    clickHouseStatus: null,
    startedAt: null,
    completedAt: "2026-06-22T00:02:00Z",
    error: null,
    shards: [
      {
        id: "anchor-shard-1",
        backupTableId: "table-orders",
        effectiveBackupType: "Full",
        parentFullBackupId: null,
        parentFullBackupTableShardId: null,
        sourceShardNumber: 1,
        sourceShardName: "s1",
        replicaNumber: 1,
        host: "source-s1",
        port: 9000,
        useTls: false,
        storagePath: "s3://bucket/full/orders/1",
        backupSizeBytes: 0,
        status: "Succeeded",
        clickHouseOperationId: null,
        clickHouseStatus: null,
        startedAt: null,
        completedAt: "2026-06-22T00:02:00Z",
        error: null,
        isPasswordProtected: false,
        passwordKeyId: null,
        passwordKeyAvailable: null
      },
      {
        id: "anchor-shard-2",
        backupTableId: "table-orders",
        effectiveBackupType: "Full",
        parentFullBackupId: null,
        parentFullBackupTableShardId: null,
        sourceShardNumber: 2,
        sourceShardName: "s2",
        replicaNumber: 1,
        host: "source-s2",
        port: 9000,
        useTls: false,
        storagePath: "s3://bucket/full/orders/2",
        backupSizeBytes: 0,
        status: "Succeeded",
        clickHouseOperationId: null,
        clickHouseStatus: null,
        startedAt: null,
        completedAt: "2026-06-22T00:02:00Z",
        error: null,
        isPasswordProtected: false,
        passwordKeyId: null,
        passwordKeyAvailable: null
      }
    ]
  }]
};

const mapping: RestoreMappingDraft = {
  backupTableId: "table-orders",
  targetDatabase: "sales_restore",
  targetTable: "orders_restore",
  append: false,
  allowSchemaMismatch: false,
  schemaOnly: false,
  createTableSqlOverride: null,
  shardSources: [],
  selected: true
};

const plan: EntityRestorePlanDto = {
  policyId: "policy-nightly",
  anchorBackupId: "backup-anchor",
  targetClusterId: "target-cluster",
  layout: "Preserve",
  cliCommand: "restore initiate-from-plan --file entity-restore-plan.json",
  cliJson: "{\"policyId\":\"policy-nightly\"}",
  queue: [
    { backupTableId: "table-orders", backupTableShardId: "latest-shard-1", database: "sales_restore", table: "orders_restore", logicalShardNumber: 1, logicalShardName: "s1", targetNode: "target-s1:9000", restoreStatement: "RESTORE TABLE sales.orders AS sales_restore.orders_restore FROM <backup-target:target>/<storage-path:redacted> ASYNC" },
    { backupTableId: "table-orders", backupTableShardId: "older-shard-2", database: "sales_restore", table: "orders_restore", logicalShardNumber: 2, logicalShardName: "s2", targetNode: "target-s2:9000", restoreStatement: "RESTORE TABLE sales.orders AS sales_restore.orders_restore FROM <backup-target:target>/<storage-path:redacted> ASYNC" }
  ],
  tables: [{
    backupTableId: "table-orders",
    sourceDatabase: "sales",
    sourceTable: "orders",
    targetDatabase: "sales_restore",
    targetTable: "orders_restore",
    append: false,
    allowSchemaMismatch: false,
    schemaOnly: false,
    candidates: [
      { backupId: "backup-latest", backupTableId: "latest-table", backupTableShardId: "latest-shard-1", backupType: "Incremental", backupStatus: "Succeeded", createdAt: "2026-06-23T00:00:00Z", sourceShardNumber: 1, sourceShardName: "s1", status: "Succeeded", isCompatible: true, isDefault: true, unavailableReason: "" },
      { backupId: "backup-anchor", backupTableId: "anchor-table", backupTableShardId: "anchor-shard-1", backupType: "Full", backupStatus: "Succeeded", createdAt: "2026-06-22T00:00:00Z", sourceShardNumber: 1, sourceShardName: "s1", status: "Succeeded", isCompatible: true, isDefault: false, unavailableReason: "" },
      { backupId: "backup-anchor", backupTableId: "anchor-table", backupTableShardId: "anchor-shard-2", backupType: "Full", backupStatus: "Succeeded", createdAt: "2026-06-22T00:00:00Z", sourceShardNumber: 2, sourceShardName: "s2", status: "Succeeded", isCompatible: true, isDefault: false, unavailableReason: "" },
      { backupId: "backup-latest", backupTableId: "latest-table", backupTableShardId: "latest-shard-2", backupType: "Incremental", backupStatus: "Succeeded", createdAt: "2026-06-23T00:00:00Z", sourceShardNumber: 2, sourceShardName: "s2", status: "Succeeded", isCompatible: false, isDefault: false, unavailableReason: "Schema does not match the anchor table." },
      { backupId: "backup-older", backupTableId: "older-table", backupTableShardId: "older-shard-2", backupType: "Full", backupStatus: "Succeeded", createdAt: "2026-06-21T00:00:00Z", sourceShardNumber: 2, sourceShardName: "s2", status: "Succeeded", isCompatible: true, isDefault: true, unavailableReason: "" }
    ],
    shards: [
      { backupTableId: "table-orders", backupTableShardId: "latest-shard-1", sourceBackupId: "backup-latest", sourceBackupType: "Incremental", sourceBackupCreatedAt: "2026-06-23T00:00:00Z", sourceShardNumber: 1, sourceShardName: "s1", targetShardNumber: 1, targetShardName: "s1", targetReplicaNumber: 1, targetHost: "target-s1", targetPort: 9000, layoutRole: "Preserve", restoreStatement: "RESTORE TABLE sales.orders AS sales_restore.orders_restore FROM <backup-target:target>/<storage-path:redacted> ASYNC" },
      { backupTableId: "table-orders", backupTableShardId: "older-shard-2", sourceBackupId: "backup-older", sourceBackupType: "Full", sourceBackupCreatedAt: "2026-06-21T00:00:00Z", sourceShardNumber: 2, sourceShardName: "s2", targetShardNumber: 2, targetShardName: "s2", targetReplicaNumber: 1, targetHost: "target-s2", targetPort: 9000, layoutRole: "Preserve", restoreStatement: "RESTORE TABLE sales.orders AS sales_restore.orders_restore FROM <backup-target:target>/<storage-path:redacted> ASYNC" }
    ]
  }]
};

describe("entity restore wizard steps", () => {
  it("filters source backups with date presets instead of a policy selector", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const onDateFilterChange = vi.fn();
    const onPreset = vi.fn();
    const onOpenBackup = vi.fn();

    await act(async () => {
      root.render(<BackupChoiceStep backups={[backup]} selectedBackupId="" onSelect={vi.fn()} onOpenBackup={onOpenBackup} clusterName={() => "Source"} policyName={() => "Nightly"} dateFilterValue="2026-06-27" activeWindowHours={72} onDateFilterChange={onDateFilterChange} onPreset={onPreset} />);
    });

    expect(host.textContent).toContain("Choose source backup");
    expect(host.textContent).toContain("Backup ID");
    expect(host.textContent).toContain("Policy");
    expect(host.textContent).toContain("Nightly");
    expect(host.textContent).toContain("3 days");
    expect(host.textContent).toContain("12h");
    expect(host.querySelector('label')?.textContent).toContain("From date");
    const fromDate = host.querySelector('input[aria-label="Filter backups from date"]') as HTMLInputElement;
    await changeInput(fromDate, "2026-06-29");
    expect(onDateFilterChange).toHaveBeenCalledWith("2026-06-29");
    const backupLink = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "backup-a") as HTMLButtonElement;
    expect(backupLink.title).toBe("backup-anchor");
    await click(backupLink);
    expect(onOpenBackup).toHaveBeenCalledWith("backup-anchor");
    const oneWeek = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "1 week") as HTMLButtonElement;
    await click(oneWeek);
    expect(onPreset).toHaveBeenCalledWith(168);

    await act(async () => root.unmount());
    host.remove();
  });

  it("keeps source backup date filters available when no backups match", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const onDateFilterChange = vi.fn();

    await act(async () => {
      root.render(<BackupChoiceStep backups={[]} selectedBackupId="" onSelect={vi.fn()} onOpenBackup={vi.fn()} clusterName={() => "Source"} policyName={() => "Manual"} dateFilterValue="2026-06-29" activeWindowHours={null} onDateFilterChange={onDateFilterChange} onPreset={vi.fn()} />);
    });

    expect(host.textContent).toContain("No backups match this date filter");
    const fromDate = host.querySelector('input[aria-label="Filter backups from date"]') as HTMLInputElement;
    expect(fromDate).toBeTruthy();
    await changeInput(fromDate, "2026-06-20");
    expect(onDateFilterChange).toHaveBeenCalledWith("2026-06-20");

    await act(async () => root.unmount());
    host.remove();
  });


  it("applies Restore to date using the latest compatible shard backup on or before the date", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const onMappingsChange = vi.fn();
    const schemaByTableId = new Map<string, SchemaTableDto>();

    await act(async () => {
      root.render(<ScopeStep backup={backup} mappings={[mapping]} onMappingsChange={onMappingsChange} sourceShardOptions={[{ value: 1, label: "1 (s1)" }, { value: 2, label: "2 (s2)" }]} selectedSourceShards={[1, 2]} onSourceShardsChange={vi.fn()} schemaByTableId={schemaByTableId} schemaLoading={false} plan={plan} restoreToDate="" onRestoreToDateChange={vi.fn()} />);
    });

    const date = host.querySelector('input[aria-label="Restore to date"]') as HTMLInputElement;
    expect(date).toBeTruthy();
    await changeInput(date, "2026-06-22");

    expect(onMappingsChange).toHaveBeenCalledWith([expect.objectContaining<Partial<RestoreTableMappingRequest>>({
      shardSources: [
        { sourceShardNumber: 1, backupTableShardId: "anchor-shard-1" },
        { sourceShardNumber: 2, backupTableShardId: "anchor-shard-2" }
      ]
    })]);

    await act(async () => root.unmount());
    host.remove();
  });
  it("allows typing Restore to date before the restore plan finishes loading", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const onRestoreToDateChange = vi.fn();
    const schemaByTableId = new Map<string, SchemaTableDto>();

    await act(async () => {
      root.render(<ScopeStep backup={backup} mappings={[mapping]} onMappingsChange={vi.fn()} sourceShardOptions={[{ value: 1, label: "1 (s1)" }, { value: 2, label: "2 (s2)" }]} selectedSourceShards={[1, 2]} onSourceShardsChange={vi.fn()} schemaByTableId={schemaByTableId} schemaLoading={false} plan={null} planLoading={true} restoreToDate="" onRestoreToDateChange={onRestoreToDateChange} />);
    });

    const date = host.querySelector('input[aria-label="Restore to date"]') as HTMLInputElement;
    expect(date).toBeTruthy();
    expect(date.disabled).toBe(false);
    expect(host.textContent).toContain("Available shard backup dates are loading.");
    await changeInput(date, "2026-06-22");

    expect(onRestoreToDateChange).toHaveBeenCalledWith("2026-06-22");

    await act(async () => root.unmount());
    host.remove();
  });

  it("keeps shard backup source details visible while the restore plan is loading", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const schemaByTableId = new Map<string, SchemaTableDto>([["table-orders", { backupTableId: "table-orders", database: "sales", table: "orders", engine: "MergeTree", dataBackedUp: true, columnsJson: "[]", createTableSql: "CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id" }]]);

    await act(async () => {
      root.render(<ScopeStep backup={backup} mappings={[mapping]} onMappingsChange={vi.fn()} sourceShardOptions={[{ value: 1, label: "1 (s1)" }, { value: 2, label: "2 (s2)" }]} selectedSourceShards={[1, 2]} onSourceShardsChange={vi.fn()} schemaByTableId={schemaByTableId} schemaLoading={false} plan={null} planLoading={true} />);
    });

    const details = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "Details") as HTMLButtonElement;
    await click(details);

    expect(host.textContent).toContain("Shard backup sources");
    expect(host.textContent).toContain("Shard backup choices are loading.");
    expect(host.textContent).toContain("Override CREATE TABLE statement");

    await act(async () => root.unmount());
    host.remove();
  });

  it("shows selected anchor backup shards when no entity plan is available", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const schemaByTableId = new Map<string, SchemaTableDto>([["table-orders", { backupTableId: "table-orders", database: "sales", table: "orders", engine: "MergeTree", dataBackedUp: true, columnsJson: "[]", createTableSql: "CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id" }]]);

    await act(async () => {
      root.render(<ScopeStep backup={backup} mappings={[mapping]} onMappingsChange={vi.fn()} sourceShardOptions={[{ value: 1, label: "1 (s1)" }, { value: 2, label: "2 (s2)" }]} selectedSourceShards={[1, 2]} onSourceShardsChange={vi.fn()} schemaByTableId={schemaByTableId} schemaLoading={false} plan={null} planLoading={false} />);
    });

    const details = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "Details") as HTMLButtonElement;
    await click(details);

    expect(host.textContent).toContain("Shard backup sources");
    expect(host.textContent).toContain("1 (s1)");
    expect(host.textContent).toContain("2 (s2)");
    expect(host.textContent).toContain("Full · selected anchor backup");
    expect(host.textContent).not.toContain("Choose a target and table to load shard backup choices.");

    await act(async () => root.unmount());
    host.remove();
  });

  it("opens Details with schema SQL editing and per-shard backup choices", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const onMappingsChange = vi.fn();
    const schemaByTableId = new Map<string, SchemaTableDto>([["table-orders", { backupTableId: "table-orders", database: "sales", table: "orders", engine: "MergeTree", dataBackedUp: true, columnsJson: "[]", createTableSql: "CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id" }]]);

    await act(async () => {
      root.render(<ScopeStep backup={backup} mappings={[mapping]} onMappingsChange={onMappingsChange} sourceShardOptions={[{ value: 1, label: "1 (s1)" }, { value: 2, label: "2 (s2)" }]} selectedSourceShards={[1, 2]} onSourceShardsChange={vi.fn()} schemaByTableId={schemaByTableId} schemaLoading={false} plan={plan} />);
    });

    expect(host.textContent).toContain("Advanced");
    const details = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "Details") as HTMLButtonElement;
    await click(details);

    expect(host.textContent).toContain("Shard backup sources");
    expect(host.textContent).toContain("Backup for all shards");
    expect(host.querySelector('.shard-source-table')).toBeTruthy();
    expect(host.querySelector('.shard-source-panel .grid-toolbar')).toBeFalsy();
    expect(host.textContent).toContain("Schema does not match the anchor table.");
    const override = host.querySelector(".restore-advanced-toggle input") as HTMLInputElement;
    await click(override);
    const sql = host.querySelector('textarea[aria-label="Custom CREATE TABLE SQL for sales.orders"]') as HTMLTextAreaElement;
    expect(sql.value).toContain("CREATE TABLE sales.orders");

    const backupForAll = Array.from(host.querySelectorAll("select")).find((select) => Array.from(select.options).some((option) => option.value === "backup-anchor")) as HTMLSelectElement;
    await change(backupForAll, "backup-anchor");
    const apply = Array.from(host.querySelectorAll("button")).find((button) => button.textContent === "Apply") as HTMLButtonElement;
    await click(apply);

    expect(onMappingsChange).toHaveBeenCalledWith([expect.objectContaining<Partial<RestoreTableMappingRequest>>({
      createTableSqlOverride: expect.stringContaining("CREATE TABLE sales.orders"),
      shardSources: [
        { sourceShardNumber: 1, backupTableShardId: "anchor-shard-1" },
        { sourceShardNumber: 2, backupTableShardId: "anchor-shard-2" }
      ]
    })]);

    await act(async () => root.unmount());
    host.remove();
  });

  it("shows final queue rows and keeps CLI replay collapsed by default", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => {
      root.render(<ReviewStep backup={backup} targetClusterName="Target" request={{ backupId: "backup-anchor", targetClusterId: "target-cluster", append: false, allowSchemaMismatch: false, layout: "Preserve", schemaOnly: false, confirmDestructive: false, clickHouseRestoreSettings: {} }} mappings={[mapping]} sourceShardOptions={[{ value: 1, label: "1 (s1)" }, { value: 2, label: "2 (s2)" }]} selectedSourceShards={[1, 2]} targetShardOptions={[]} selectedTargetShards={[]} errors={[]} plan={plan} />);
    });

    expect(host.textContent).toContain("Final queue");
    expect(host.textContent).toContain("target-s1:9000");
    expect(host.textContent).toContain("Incremental");
    expect(host.textContent).toContain("Full");
    expect(host.textContent).toContain("RESTORE TABLE sales.orders");
    expect(host.textContent).toContain("CLI replay command");
    const replay = host.querySelector(".restore-review-collapsible") as HTMLDetailsElement;
    expect(replay).toBeTruthy();
    expect(replay.open).toBe(false);
    expect(host.querySelector('textarea[aria-label="Restore plan JSON"]')).toBeTruthy();
    expect(host.textContent).toContain("restore initiate-from-plan --file entity-restore-plan.json");

    await act(async () => root.unmount());
    host.remove();
  });
});

async function click(element: HTMLElement) {
  await act(async () => {
    element.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  });
}

async function changeInput(element: HTMLInputElement, value: string) {
  await act(async () => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
    setter?.call(element, value);
    element.dispatchEvent(new Event("input", { bubbles: true }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
  });
}
async function change(element: HTMLSelectElement, value: string) {
  await act(async () => {
    element.value = value;
    element.dispatchEvent(new Event("change", { bubbles: true }));
  });
}
