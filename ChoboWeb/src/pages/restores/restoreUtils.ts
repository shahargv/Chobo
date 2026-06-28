import type { BackupDto, BackupRunStatus, ClickHouseClusterShardDto, InitiateRestoreRequest, RestoreLayout, RestoreRunStatus } from "../../api/generated";
import type { RestoreMappingDraft, RestoreStep, SourceShardOption, TargetShardOption } from "./restoreTypes";

export function validateStep(step: RestoreStep, request: InitiateRestoreRequest, mappings: RestoreMappingDraft[], selectedSourceShards: number[], sourceShardCount: number, selectedTargetShards: number[] = [], targetShardCount = 0, preserveLayoutError: string | null = null) {
  if (step === 0) return request.backupId ? [] : ["Choose a backup."];
  if (step === 1) return validateRestoreRequest(request, mappings, selectedSourceShards, sourceShardCount, selectedTargetShards, targetShardCount, preserveLayoutError).filter((error) => error.startsWith("Choose a target") || error.startsWith("Choose at least one target") || error.startsWith("Preserve layout"));
  if (step === 2) return validateRestoreRequest(request, mappings, selectedSourceShards, sourceShardCount, selectedTargetShards, targetShardCount, preserveLayoutError).filter((error) => !error.startsWith("Choose a backup") && !error.startsWith("Choose a target") && !error.startsWith("Choose at least one target"));
  return validateRestoreRequest(request, mappings, selectedSourceShards, sourceShardCount, selectedTargetShards, targetShardCount, preserveLayoutError);
}

export function isBackupRestorable(backup: BackupDto | null | undefined) {
  return backup ? isBackupStatusRestorable(backup.status) : false;
}

export function isBackupStatusRestorable(status: BackupRunStatus) {
  return status === "Succeeded" || status === "PartiallySucceeded";
}
export function validateRestoreRequest(request: InitiateRestoreRequest, mappings: RestoreMappingDraft[], selectedSourceShards: number[], sourceShardCount: number, selectedTargetShards: number[] = [], targetShardCount = 0, preserveLayoutError: string | null = null) {
  const errors: string[] = [];
  if (!request.backupId) errors.push("Choose a backup.");
  if (!request.targetClusterId) errors.push("Choose a target ClickHouse cluster.");
  if ((request.layout ?? "Preserve") === "Redistribute" && targetShardCount > 0 && selectedTargetShards.length === 0) errors.push("Choose at least one target shard for redistribute.");
  if ((request.layout ?? "Preserve") === "Preserve" && preserveLayoutError) errors.push(preserveLayoutError);
  if (!request.schemaOnly && sourceShardCount > 0 && selectedSourceShards.length === 0) errors.push("Choose at least one source shard.");
  const selected = mappings.filter((mapping) => mapping.selected);
  if (selected.length === 0) errors.push("Choose at least one table to restore.");
  selected.forEach((mapping) => {
    if (!mapping.targetDatabase?.trim() || !mapping.targetTable?.trim()) {
      errors.push("Every selected table needs a target database and target table.");
    }
    if (mapping.schemaOnly && mapping.append) {
      errors.push("Schema-only table restores cannot append data.");
    }
    if (mapping.createTableSqlOverride != null) {
      const sql = mapping.createTableSqlOverride.trim();
      if (!sql) errors.push("Custom CREATE TABLE SQL must not be empty.");
      if (sql && !isSingleCreateTableStatement(sql)) errors.push("Custom CREATE TABLE SQL must be a single CREATE TABLE statement.");
    }
  });
  return [...new Set(errors)];
}

function isSingleCreateTableStatement(sql: string) {
  if (!sql.toLowerCase().startsWith("create table ")) return false;
  const withoutTrailingSemicolon = sql.endsWith(";") ? sql.slice(0, -1).trimEnd() : sql;
  return !withoutTrailingSemicolon.includes(";");
}
export function getSourceShardOptions(backup: BackupDto | null): SourceShardOption[] {
  const shards = new Map<number, string>();
  backup?.tables.forEach((table) => {
    table.shards.forEach((shard) => {
      shards.set(shard.sourceShardNumber, shard.sourceShardName ? `${shard.sourceShardNumber} (${shard.sourceShardName})` : `${shard.sourceShardNumber}`);
    });
  });

  return [...shards.entries()]
    .sort(([left], [right]) => left - right)
    .map(([value, label]) => ({ value, label }));
}

export function getMissingPreserveTargetShards(selectedSourceShards: number[], targetShardOptions: TargetShardOption[]) {
  const available = new Set(targetShardOptions.map((shard) => shard.value));
  return selectedSourceShards.filter((shard) => !available.has(shard));
}

export function getTargetShardOptions(topology: ClickHouseClusterShardDto[] | undefined): TargetShardOption[] {
  const shards = new Map<number, string>();
  topology?.forEach((shard) => {
    if (!shards.has(shard.shardNumber)) {
      shards.set(shard.shardNumber, shard.shardName ? `${shard.shardNumber} (${shard.shardName})` : `${shard.shardNumber}`);
    }
  });

  return [...shards.entries()]
    .sort(([left], [right]) => left - right)
    .map(([value, label]) => ({ value, label }));
}

export function restoreTargetTableName(table: string) {
  return table.endsWith("_restore") ? table : `${table}_restore`;
}

export function formatRestoreLayout(layout: RestoreLayout) {
  if (layout === "SingleNode") return "Single node";
  return layout;
}

export function formatShardSelection(shards: SourceShardOption[], selected: number[], shardKind = "source") {
  if (shards.length === 0) return `No ${shardKind} shard filter`;
  if (selected.length === shards.length) return `All ${shardKind} shards`;
  if (selected.length === 0) return "No shards selected";
  return selected.join(", ");
}

export function buildImpactSentence(request: InitiateRestoreRequest, mappings: RestoreMappingDraft[], sourceShardOptions: SourceShardOption[], selectedSourceShards: number[], targetShardOptions: TargetShardOption[] = [], selectedTargetShards: number[] = []) {
  const mode = formatRestoreLayout(request.layout ?? "Preserve").toLowerCase();
  const shardText = formatShardSelection(sourceShardOptions, selectedSourceShards).toLowerCase();
  const targetText = (request.layout ?? "Preserve") === "Redistribute" && targetShardOptions.length > 0
    ? ` onto ${formatShardSelection(targetShardOptions, selectedTargetShards, "target").toLowerCase()}`
    : "";
  const appendCount = mappings.filter((mapping) => mapping.append && !mapping.schemaOnly).length;
  const schemaOnlyCount = mappings.filter((mapping) => mapping.schemaOnly).length;
  const mismatchCount = mappings.filter((mapping) => mapping.allowSchemaMismatch).length;
  const riskText = [
    appendCount ? `${appendCount} append` : null,
    schemaOnlyCount ? `${schemaOnlyCount} schema-only` : null,
    mismatchCount ? `${mismatchCount} schema-mismatch allowed` : null
  ].filter(Boolean).join(", ") || "no risky table options";
  return `Restore ${mappings.length} table${mappings.length === 1 ? "" : "s"} using ${mode} layout from ${shardText}${targetText}; ${riskText}.`;
}

export function formatTableOptions(append: boolean, allowSchemaMismatch: boolean) {
  const options = [];
  if (append) options.push("Append");
  if (allowSchemaMismatch) options.push("Schema mismatch allowed");
  return options.length === 0 ? "Default" : options.join("; ");
}

export function formatTimeSeconds(value?: string | null) {
  if (!value) return "never";
  return new Date(value).toLocaleString(undefined, {
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit"
  });
}

export function isRestoreInExecutionPhase(status: RestoreRunStatus | undefined) {
  return status === "Queued" || status === "Running";
}

export function getRequestedBackupId(state: unknown) {
  if (!state || typeof state !== "object" || !("backupId" in state)) return null;
  const backupId = (state as { backupId?: unknown }).backupId;
  return typeof backupId === "string" ? backupId : null;
}



