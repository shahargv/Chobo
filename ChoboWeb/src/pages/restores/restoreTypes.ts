import type { RestoreTableMappingRequest } from "../../api/generated";

export type RestoreMappingDraft = RestoreTableMappingRequest & { selected: boolean; createTableSqlOverride?: string | null };
export type SourceShardOption = { value: number; label: string };
export type TargetShardOption = SourceShardOption;
export type RestoreStep = 0 | 1 | 2 | 3;

export const restoreSteps = [
  { title: "Source backup", body: "Pick the recovery point." },
  { title: "Destination", body: "Choose where and how it lands." },
  { title: "Scope", body: "Select shards and tables." },
  { title: "Review", body: "Confirm the impact before queueing." }
] as const;

