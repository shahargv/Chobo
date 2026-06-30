import { useEffect, useState, type ReactNode } from "react";
import { ArrowRight, CheckCircle2, Code2, Database, GitBranch, Info, ListChecks, ShieldAlert, SlidersHorizontal } from "lucide-react";
import type { BackupDto, EntityRestorePlanDto, InitiateRestoreRequest, RestoreLayout, SchemaTableDto } from "../../api/generated";
import { DataTable, Detail, Empty, Select, Status } from "../../components/ui";
import { formatTime } from "../../utils/format";
import type { RestoreMappingDraft, RestoreStep, SourceShardOption, TargetShardOption } from "./restoreTypes";
import { restoreSteps } from "./restoreTypes";
import { buildImpactSentence, formatRestoreLayout, formatShardSelection, restoreTargetTableName } from "./restoreUtils";

export function RestoreStepper({ step, errors, onStep }: { step: RestoreStep; errors: string[]; onStep: (step: RestoreStep) => void }) {
  return (
    <div className="restore-stepper" aria-label="Restore steps">
      {restoreSteps.map((item, index) => {
        const isActive = step === index;
        const isDone = step > index && errors.length === 0;
        return <button key={item.title} type="button" className={`restore-step ${isActive ? "active" : ""} ${isDone ? "done" : ""}`} onClick={() => onStep(index as RestoreStep)}>
          <span className="restore-step-number">{isDone ? <CheckCircle2 size={15} /> : index + 1}</span>
          <span><strong>{item.title}</strong><small>{item.body}</small></span>
        </button>;
      })}
    </div>
  );
}

export function BackupChoiceStep({ backups, selectedBackupId, onSelect, clusterName, dateFilterValue, activeWindowHours, onDateFilterChange, onPreset, isLoading = false }: { backups: BackupDto[]; selectedBackupId: string; onSelect: (backupId: string) => void; clusterName: (clusterId: string) => string; dateFilterValue: string; activeWindowHours: number | null; onDateFilterChange: (value: string) => void; onPreset: (hours: number) => void; isLoading?: boolean }) {
  return (
    <div className="restore-step-content">
      <StepIntro icon={<Database size={20} />} title="Choose source backup" body="Choose the backup or schema backup that anchors the table list and schema. Use the date window to quickly narrow the recovery points." />
      <BackupDateFilter value={dateFilterValue} activeWindowHours={activeWindowHours} onChange={onDateFilterChange} onPreset={onPreset} />
      {!isLoading && backups.length === 0
        ? <Empty text="No backups match this date filter. Widen the window or create a backup before starting a restore." />
        : <DataTable headers={["Use", "Created", "Type", "Source cluster", "Tables", "Status"]} isLoading={isLoading}>
          {backups.map((backup) => (
            <tr key={backup.id}>
              <td><input className="row-checkbox" aria-label={`Use backup ${backup.id}`} type="radio" checked={selectedBackupId === backup.id} onChange={() => onSelect(backup.id)} /></td>
              <td>{formatTime(backup.createdAt)}</td>
              <td>{backup.backupType}</td>
              <td>{clusterName(backup.sourceClusterId)}</td>
              <td>{backup.tableCount}</td>
              <td><Status value={backup.status} /></td>
            </tr>
          ))}
        </DataTable>}
    </div>
  );
}

function BackupDateFilter({ value, activeWindowHours, onChange, onPreset }: { value: string; activeWindowHours: number | null; onChange: (value: string) => void; onPreset: (hours: number) => void }) {
  const presets = [
    { label: "12h", hours: 12 },
    { label: "24h", hours: 24 },
    { label: "3 days", hours: 72 },
    { label: "1 week", hours: 168 }
  ];
  return <div className="backup-date-filter">
    <label className="restore-date-picker">From date<input aria-label="Filter backups from date" type="text" inputMode="numeric" placeholder="YYYY-MM-DD" value={value} onChange={(event) => onChange(event.target.value)} /></label>
    <div className="segmented-actions" aria-label="Backup date presets">
      {presets.map((preset) => <button key={preset.hours} type="button" className={activeWindowHours === preset.hours ? "secondary selected" : "ghost"} onClick={() => onPreset(preset.hours)}>{preset.label}</button>)}
    </div>
  </div>;
}

export function DestinationStep({ request, onChange, clusters, targetShardOptions, selectedTargetShards, onTargetShardsChange, targetShardsLoading, preserveLayoutDisabled, preserveLayoutReason }: { request: InitiateRestoreRequest; onChange: (request: InitiateRestoreRequest) => void; clusters: Array<{ id: string; name: string }>; targetShardOptions: TargetShardOption[]; selectedTargetShards: number[]; onTargetShardsChange: (value: number[]) => void; targetShardsLoading: boolean; preserveLayoutDisabled: boolean; preserveLayoutReason: string | null }) {
  return (
    <div className="restore-step-content">
      <StepIntro icon={<GitBranch size={20} />} title="Choose the destination and layout" body="The target cluster receives new or appended tables. Chobo records this as a restore operation and writes audit entries when it is queued and executed." />
      <div className="form-grid restore-destination-grid">
        <Select label="Target cluster" value={request.targetClusterId} onChange={(value) => onChange({ ...request, targetClusterId: value })} options={clusters.map((cluster) => [cluster.id, cluster.name])} />
      </div>
      <div className="restore-choice-grid">
        <LayoutChoice value="Preserve" selected={request.layout ?? "Preserve"} title="Preserve layout" body="Restore each selected source shard to the matching target shard. Available only when those shard numbers exist on the target cluster." disabled={preserveLayoutDisabled} disabledReason={preserveLayoutReason} onSelect={(layout) => onChange({ ...request, layout })} />
        <LayoutChoice value="Redistribute" selected={request.layout ?? "Preserve"} title="Redistribute" body="Spread restored data across selected target shards. Choose one shard here when you intentionally want all restored data placed there." onSelect={(layout) => onChange({ ...request, layout })} />
        <LayoutChoice value="SingleNode" selected={request.layout ?? "Preserve"} title="Single node" body="Restore into one target node. Useful for inspection, recovery drills, or extracting a small subset." onSelect={(layout) => onChange({ ...request, layout })} />
      </div>
      {(request.layout ?? "Preserve") === "Redistribute" && <TargetShardsPicker shards={targetShardOptions} selected={selectedTargetShards} onChange={onTargetShardsChange} isLoading={targetShardsLoading} />}
    </div>
  );
}

function LayoutChoice({ value, selected, title, body, disabled, disabledReason, onSelect }: { value: RestoreLayout; selected: RestoreLayout; title: string; body: string; disabled?: boolean; disabledReason?: string | null; onSelect: (value: RestoreLayout) => void }) {
  return <button type="button" disabled={disabled} className={`restore-choice ${selected === value ? "selected" : ""} ${disabled ? "disabled" : ""}`} onClick={() => onSelect(value)}><strong>{title}</strong><span>{disabled && disabledReason ? disabledReason : body}</span></button>;
}

export function ScopeStep({ backup, mappings, onMappingsChange, sourceShardOptions, selectedSourceShards, onSourceShardsChange, schemaByTableId, schemaLoading, plan, planLoading = false, planError = null, restoreToDate = "", onRestoreToDateChange }: { backup: BackupDto | null; mappings: RestoreMappingDraft[]; onMappingsChange: (mappings: RestoreMappingDraft[]) => void; sourceShardOptions: SourceShardOption[]; selectedSourceShards: number[]; onSourceShardsChange: (value: number[]) => void; schemaByTableId: Map<string, SchemaTableDto>; schemaLoading: boolean; plan?: EntityRestorePlanDto | null; planLoading?: boolean; planError?: string | null; restoreToDate?: string; onRestoreToDateChange?: (value: string) => void }) {
  const hasSelectedTables = mappings.some((mapping) => mapping.selected);
  const restoreToDateSummary = buildRestoreToDateSummary(plan, restoreToDate, hasSelectedTables, planLoading, planError);
  const applyRestoreToDate = (value: string) => {
    onRestoreToDateChange?.(value);
    const datedMappings = applyRestoreDateToMappings(mappings, plan, value);
    if (datedMappings) onMappingsChange(datedMappings);
  };
  useEffect(() => {
    if (!restoreToDate || !plan) return;
    const datedMappings = applyRestoreDateToMappings(mappings, plan, restoreToDate);
    if (datedMappings && shardSourceSignature(datedMappings) !== shardSourceSignature(mappings)) onMappingsChange(datedMappings);
  }, [onMappingsChange, plan, restoreToDate]);
  return (
    <div className="restore-step-content">
      <StepIntro icon={<ListChecks size={20} />} title="Choose exactly what gets restored" body="Select source shards and tables, then decide whether each table restores schema plus data or schema only. The default target table name uses _restore so existing tables are not overwritten by accident." />
      <SourceShardsPicker shards={sourceShardOptions} selected={selectedSourceShards} onChange={onSourceShardsChange} />
      <RestoreToDatePicker value={restoreToDate} onChange={applyRestoreToDate} disabled={false} summary={restoreToDateSummary} />
      <RestoreMappingsEditor backup={backup} mappings={mappings} onChange={onMappingsChange} schemaByTableId={schemaByTableId} schemaLoading={schemaLoading} plan={plan} planLoading={planLoading} planError={planError} />
    </div>
  );
}

export function ReviewStep({ backup, targetClusterName, request, mappings, sourceShardOptions, selectedSourceShards, targetShardOptions, selectedTargetShards, errors, plan, planError }: { backup: BackupDto | null; targetClusterName: string; request: InitiateRestoreRequest; mappings: RestoreMappingDraft[]; sourceShardOptions: SourceShardOption[]; selectedSourceShards: number[]; targetShardOptions: TargetShardOption[]; selectedTargetShards: number[]; errors: string[]; plan?: EntityRestorePlanDto | null; planError?: string | null }) {
  return (
    <div className="restore-step-content">
      <StepIntro icon={<ShieldAlert size={20} />} title="Review the restore impact" body="Queueing starts an asynchronous restore run. Review the target, layout, selected shards, table names, and risky options before continuing." />
      {errors.length > 0 && <div className="restore-warning-list">{errors.map((error) => <span className="field-error" key={error}>{error}</span>)}</div>}
      <div className="detail-list restore-review-details">
        <Detail label="Backup" value={backup ? `${backup.backupType} · ${formatTime(backup.createdAt)}` : "Not selected"} />
        <Detail label="Target cluster" value={targetClusterName || "Not selected"} />
        <Detail label="Layout" value={formatRestoreLayout(request.layout ?? "Preserve")} />
        {(request.layout ?? "Preserve") === "Redistribute" && <Detail label="Target shards" value={formatShardSelection(targetShardOptions, selectedTargetShards, "target")} />}
      </div>
      <div className="restore-review-note">
        <strong>What Chobo will do</strong>
        <span>{buildImpactSentence(request, mappings, sourceShardOptions, selectedSourceShards, targetShardOptions, selectedTargetShards)}</span>
      </div>
      <DataTable headers={["Source table", "Target table", "Mode", "Append", "Schema mismatch", "Schema SQL"]}>
        {mappings.map((mapping) => {
          const table = backup?.tables.find((item) => item.id === mapping.backupTableId);
          return <tr key={mapping.backupTableId}>
            <td>{table ? `${table.database}.${table.table}` : mapping.backupTableId}</td>
            <td>{mapping.targetDatabase}.{mapping.targetTable}</td>
            <td>{mapping.schemaOnly ? "Schema only" : "Schema + data"}</td>
            <td>{mapping.schemaOnly ? "No" : mapping.append ? "Yes" : "No"}</td>
            <td>{mapping.allowSchemaMismatch ? "Allowed" : "Blocked"}</td>
            <td>{mapping.createTableSqlOverride?.trim() ? "Custom" : "Captured"}</td>
          </tr>;
        })}
      </DataTable>
      {planError && <span className="field-error">{planError}</span>}
      {plan && <div className="restore-review-plan">
        <div className="section-head"><h3>Final queue</h3><span className="hint">These rows will become restore queue items when the restore is queued.</span></div>
        <DataTable headers={["Target table", "Shard", "Source backup", "Target node", "RESTORE statement"]}>
          {plan.queue.map((item) => {
            const source = plan.tables.flatMap((table) => table.shards).find((shard) => shard.backupTableShardId === item.backupTableShardId);
            return <tr key={`${item.backupTableShardId}-${item.logicalShardNumber}`}>
              <td>{item.database}.{item.table}</td>
              <td>{item.logicalShardName ? `${item.logicalShardNumber} (${item.logicalShardName})` : item.logicalShardNumber}</td>
              <td>{source ? `${source.sourceBackupType} · ${formatTime(source.sourceBackupCreatedAt)}` : item.backupTableShardId}</td>
              <td>{item.targetNode}</td>
              <td className="mono wide-cell">{item.restoreStatement}</td>
            </tr>;
          })}
        </DataTable>
        <div className="restore-review-note">
          <strong>CLI replay</strong>
          <span className="mono">{plan.cliCommand}</span>
        </div>
        <textarea className="create-table-override-editor" aria-label="Restore plan JSON" readOnly value={plan.cliJson} />
      </div>}    </div>
  );
}

function StepIntro({ icon, title, body }: { icon: ReactNode; title: string; body: string }) {
  return <div className="restore-step-intro"><div className="restore-step-icon">{icon}</div><div><h2>{title}</h2><p>{body}</p></div></div>;
}

export function ImpactSummary({ backup, targetClusterName, request, mappings, sourceShardOptions, selectedSourceShards, targetShardOptions, selectedTargetShards, errors }: { backup: BackupDto | null; targetClusterName: string; request: InitiateRestoreRequest; mappings: RestoreMappingDraft[]; sourceShardOptions: SourceShardOption[]; selectedSourceShards: number[]; targetShardOptions: TargetShardOption[]; selectedTargetShards: number[]; errors: string[] }) {
  const riskyOptions = mappings.filter((mapping) => mapping.append || mapping.allowSchemaMismatch).length;
  return (
    <aside className="panel restore-impact-panel" aria-label="Restore impact summary">
      <div>
        <h2>Impact summary</h2>
        <p>Keep this open as you move through the wizard. It reflects the restore request that will be queued.</p>
      </div>
      <div className="restore-impact-list">
        <ImpactItem label="Recovery point" value={backup ? `${backup.backupType} from ${formatTime(backup.createdAt)}` : "Choose a backup"} complete={!!backup} />
        <ImpactItem label="Destination" value={request.targetClusterId ? targetClusterName : "Choose a target cluster"} complete={!!request.targetClusterId} />
        <ImpactItem label="Layout" value={formatRestoreLayout(request.layout ?? "Preserve")} complete />
        <ImpactItem label="Source shards" value={formatShardSelection(sourceShardOptions, selectedSourceShards)} complete={sourceShardOptions.length === 0 || selectedSourceShards.length > 0} />
        {(request.layout ?? "Preserve") === "Redistribute" && <ImpactItem label="Target shards" value={formatShardSelection(targetShardOptions, selectedTargetShards, "target")} complete={targetShardOptions.length === 0 || selectedTargetShards.length > 0} />}
        <ImpactItem label="Tables" value={`${mappings.length} selected`} complete={mappings.length > 0} />
        <ImpactItem label="Risk options" value={riskyOptions === 0 ? "None enabled" : `${riskyOptions} table${riskyOptions === 1 ? "" : "s"} use append or mismatch`} complete={riskyOptions === 0} warn={riskyOptions > 0} />
      </div>
      <div className="restore-review-note">
        <strong>Audit trail</strong>
        <span>Queueing this restore records who requested it, which backup was used, the target cluster, selected tables, layout, and options.</span>
      </div>
      {errors.length > 0 && <div className="restore-warning-list">{errors.map((error) => <span className="field-error" key={error}>{error}</span>)}</div>}
    </aside>
  );
}

function ImpactItem({ label, value, complete, warn }: { label: string; value: string; complete: boolean; warn?: boolean }) {
  return <div className={`restore-impact-item ${complete ? "complete" : ""} ${warn ? "warn" : ""}`}><span>{label}</span><strong>{value}</strong></div>;
}


function RestoreToDatePicker({ value, onChange, disabled, summary }: { value: string; onChange: (value: string) => void; disabled: boolean; summary: string }) {
  return <div className="restore-date-panel">
    <label className="restore-date-picker">Restore to date<input aria-label="Restore to date" type="text" inputMode="numeric" placeholder="YYYY-MM-DD" value={value} disabled={disabled} onChange={(event) => onChange(event.target.value)} /></label>
    <span className="hint">{summary}</span>
    {value && <button type="button" className="ghost" onClick={() => onChange("")}>Use latest defaults</button>}
  </div>;
}

function applyRestoreDateToMappings(mappings: RestoreMappingDraft[], plan: EntityRestorePlanDto | null | undefined, dateValue: string): RestoreMappingDraft[] | null {
  if (!plan) return null;
  if (!dateValue) {
    return mappings.map((mapping) => ({ ...mapping, shardSources: [] }));
  }

  const cutoff = restoreDateCutoff(dateValue);
  if (!cutoff) return null;
  return mappings.map((mapping) => {
    const planTable = plan.tables.find((table) => table.backupTableId === mapping.backupTableId);
    if (!planTable) return mapping;
    const shardSources = planTable.shards.map((shard) => {
      const candidate = latestCandidateAtOrBefore(planTable.candidates, shard.sourceShardNumber, cutoff);
      return candidate ? { sourceShardNumber: shard.sourceShardNumber, backupTableShardId: candidate.backupTableShardId } : null;
    }).filter((item): item is { sourceShardNumber: number; backupTableShardId: string } => item !== null);
    return { ...mapping, shardSources };
  });
}

function buildRestoreToDateSummary(plan: EntityRestorePlanDto | null | undefined, dateValue: string, hasSelectedTables: boolean, planLoading: boolean, planError: string | null) {
  if (!hasSelectedTables) return "Select at least one table to load available backup dates.";
  if (planError) return "Available shard backup dates could not be loaded.";
  if (!plan) return planLoading ? "Available shard backup dates are loading." : "Choose a target to load available backup dates.";
  if (!dateValue) return "No date override. Chobo will use the latest compatible backup for each shard.";
  const cutoff = restoreDateCutoff(dateValue);
  if (!cutoff) return "Choose a valid date.";
  const totalShards = plan.tables.reduce((total, table) => total + table.shards.length, 0);
  const matched = plan.tables.reduce((total, table) => total + table.shards.filter((shard) => latestCandidateAtOrBefore(table.candidates, shard.sourceShardNumber, cutoff)).length, 0);
  return matched === totalShards
    ? `Using the latest compatible shard backup on or before ${dateValue}.`
    : `${matched}/${totalShards} shards have a compatible backup on or before ${dateValue}.`;
}

function latestCandidateAtOrBefore(candidates: EntityRestorePlanDto["tables"][number]["candidates"], sourceShardNumber: number, cutoff: Date) {
  return candidates
    .filter((candidate) => candidate.isCompatible && candidate.sourceShardNumber === sourceShardNumber && new Date(candidate.createdAt).getTime() <= cutoff.getTime())
    .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())[0] ?? null;
}

function restoreDateCutoff(dateValue: string) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(dateValue)) return null;
  const cutoff = new Date(`${dateValue}T23:59:59.999`);
  return Number.isNaN(cutoff.getTime()) ? null : cutoff;
}
function shardSourceSignature(mappings: RestoreMappingDraft[]) {
  return JSON.stringify(mappings.map((mapping) => [mapping.backupTableId, [...(mapping.shardSources ?? [])].sort((left, right) => left.sourceShardNumber - right.sourceShardNumber)]));
}

function RestoreMappingsEditor({ backup, mappings, onChange, schemaByTableId, schemaLoading, plan, planLoading, planError }: { backup: BackupDto | null; mappings: RestoreMappingDraft[]; onChange: (mappings: RestoreMappingDraft[]) => void; schemaByTableId: Map<string, SchemaTableDto>; schemaLoading: boolean; plan?: EntityRestorePlanDto | null; planLoading?: boolean; planError?: string | null }) {
  const [advancedTableId, setAdvancedTableId] = useState<string | null>(null);
  if (!backup) return <Empty text="Choose a backup first. Its tables will appear here." />;
  if (backup.tables.length === 0) return <Empty text="This backup does not contain restorable tables." />;
  const update = (id: string, patch: Partial<RestoreMappingDraft>) => onChange(mappings.map((mapping) => mapping.backupTableId === id ? { ...mapping, ...patch } : mapping));
  const mapped = (tableId: string, database: string, table: string) =>
    mappings.find((item) => item.backupTableId === tableId) ?? { backupTableId: tableId, targetDatabase: database, targetTable: restoreTargetTableName(table), append: false, allowSchemaMismatch: false, schemaOnly: false, shardSources: [], selected: false, createTableSqlOverride: null };
  return (
    <div className="restore-mapping-editor">
      <div className="section-head">
        <div>
          <h3>Table mappings</h3>
          <span className="hint">Checked tables will be restored. Target names default to the source table plus _restore to avoid accidental replacement.</span>
        </div>
        <div className="actions">
          <button type="button" className="secondary" onClick={() => onChange(mappings.map((mapping) => ({ ...mapping, selected: true, targetTable: mapping.targetTable || restoreTargetTableName(backup.tables.find((table) => table.id === mapping.backupTableId)?.table ?? "") })))}>Check all</button>
          <button type="button" className="ghost" onClick={() => onChange(mappings.map((mapping) => ({ ...mapping, selected: false })))}>Clear</button>
        </div>
      </div>
      <DataTable headers={["Restore", "Source table", "Target database", "Target table", "Mode", "Options", "Advanced"]}>
        {backup.tables.map((table) => {
          const mapping = mapped(table.id, table.database, table.table);
          return <tr key={table.id}>
            <td><input className="row-checkbox" type="checkbox" checked={mapping.selected} onChange={(event) => update(table.id, { selected: event.target.checked })} /></td>
            <td><span className="restore-source-table-name">{table.database}.{table.table}{isDistributedEngine(table.engine) && <span className="info-icon" title="Distributed table restore restores the table declaration only. No data is restored for the Distributed engine table itself, and the declaration will point to the same underlying tables as the backup."><Info size={15} /></span>}</span></td>
            <td><input value={mapping.targetDatabase ?? ""} disabled={!mapping.selected} onChange={(event) => update(table.id, { targetDatabase: event.target.value })} /></td>
            <td><input value={mapping.targetTable ?? ""} disabled={!mapping.selected} onChange={(event) => update(table.id, { targetTable: event.target.value })} /></td>
            <td>
              {table.dataBackedUp
                ? <select value={mapping.schemaOnly ? "schema" : "data"} disabled={!mapping.selected} onChange={(event) => update(table.id, { schemaOnly: event.target.value === "schema", append: event.target.value === "schema" ? false : mapping.append })}>
                  <option value="data">Schema + data</option>
                  <option value="schema">Schema only</option>
                </select>
                : <span className="chip">Schema only</span>}
            </td>
            <td>
              <div className="restore-table-options">
                <label className="checkbox-row"><input type="checkbox" checked={mapping.append ?? false} disabled={!mapping.selected || mapping.schemaOnly || !table.dataBackedUp} onChange={(event) => update(table.id, { append: event.target.checked })} /> Append to existing table</label>
                <label className="checkbox-row"><input type="checkbox" checked={mapping.allowSchemaMismatch ?? false} disabled={!mapping.selected} onChange={(event) => update(table.id, { allowSchemaMismatch: event.target.checked })} /> Allow schema mismatch</label>
              </div>
            </td>
            <td>
              <button type="button" className={mapping.createTableSqlOverride?.trim() ? "secondary advanced-active" : "ghost"} disabled={!mapping.selected} title="Schema and shard source details" onClick={() => setAdvancedTableId(table.id)}>
                {mapping.createTableSqlOverride?.trim() ? <Code2 size={15} /> : <SlidersHorizontal size={15} />}
                {mapping.createTableSqlOverride?.trim() || (mapping.shardSources?.length ?? 0) > 0 ? "Details" : "Details"}
              </button>
            </td>
          </tr>;
        })}
      </DataTable>
      {advancedTableId && <CreateTableOverrideDialog table={backup.tables.find((table) => table.id === advancedTableId) ?? null} mapping={mappings.find((mapping) => mapping.backupTableId === advancedTableId) ?? null} capturedSql={schemaByTableId.get(advancedTableId)?.createTableSql ?? ""} schemaLoading={schemaLoading} planTable={plan?.tables.find((table) => table.backupTableId === advancedTableId) ?? null} planLoading={planLoading} planError={planError} onSave={(patch) => { update(advancedTableId, patch); setAdvancedTableId(null); }} onClose={() => setAdvancedTableId(null)} />}
    </div>
  );
}

function CreateTableOverrideDialog({ table, mapping, capturedSql, schemaLoading, planTable, planLoading = false, planError = null, onSave, onClose }: { table: BackupDto["tables"][number] | null; mapping: RestoreMappingDraft | null; capturedSql: string; schemaLoading: boolean; planTable?: EntityRestorePlanDto["tables"][number] | null; planLoading?: boolean; planError?: string | null; onSave: (patch: Partial<RestoreMappingDraft>) => void; onClose: () => void }) {
  const [enabled, setEnabled] = useState(() => mapping?.createTableSqlOverride != null);
  const [sql, setSql] = useState(() => mapping?.createTableSqlOverride ?? capturedSql);
  const [shardSources, setShardSources] = useState(() => mapping?.shardSources ?? []);
  const targetName = mapping ? `${mapping.targetDatabase}.${mapping.targetTable}` : "target table";
  const sourceName = table ? `${table.database}.${table.table}` : "source table";
  const canSave = !enabled || sql.trim().length > 0;
  const setOverride = (checked: boolean) => {
    setEnabled(checked);
    if (checked && !sql.trim()) setSql(capturedSql);
  };
  const planShards = planTable?.shards ?? [];
  const showShardSources = Boolean(table?.dataBackedUp && !mapping?.schemaOnly);
  const candidatesByShard = new Map<number, NonNullable<typeof planTable>["candidates"]>();
  for (const candidate of planTable?.candidates ?? []) {
    candidatesByShard.set(candidate.sourceShardNumber, [...(candidatesByShard.get(candidate.sourceShardNumber) ?? []), candidate]);
  }
  const selectedSourceForShard = (sourceShardNumber: number) => shardSources.find((source) => source.sourceShardNumber === sourceShardNumber)?.backupTableShardId ?? planShards.find((shard) => shard.sourceShardNumber === sourceShardNumber)?.backupTableShardId ?? "";
  const setSourceForShard = (sourceShardNumber: number, backupTableShardId: string) => {
    setShardSources((current) => {
      const next = current.filter((source) => source.sourceShardNumber !== sourceShardNumber);
      if (backupTableShardId) next.push({ sourceShardNumber, backupTableShardId });
      return next.sort((a, b) => a.sourceShardNumber - b.sourceShardNumber);
    });
  };
  const setOneBackupForAll = (backupId: string) => {
    if (!planTable || !backupId) return;
    setShardSources(planShards.map((shard) => {
      const candidate = planTable.candidates.find((item) => item.backupId === backupId && item.sourceShardNumber === shard.sourceShardNumber && item.isCompatible);
      return candidate ? { sourceShardNumber: shard.sourceShardNumber, backupTableShardId: candidate.backupTableShardId } : null;
    }).filter((item): item is { sourceShardNumber: number; backupTableShardId: string } => item !== null));
  };
  return <div className="modal-backdrop" role="presentation" onClick={onClose}>
    <section className="confirm-dialog create-table-override-dialog" role="dialog" aria-modal="true" aria-labelledby="create-table-override-title" onClick={(event) => event.stopPropagation()}>
      <div className="confirm-icon primary"><Code2 size={22} /></div>
      <div className="confirm-content">
        <div className="section-head">
          <div>
            <h2 id="create-table-override-title">Table details</h2>
            <span className="hint">{sourceName} to {targetName}</span>
          </div>
          <button type="button" className="ghost" onClick={onClose}>Close</button>
        </div>
        {showShardSources && <div className="shard-source-panel">
          <div className="section-head"><h3>Shard backup sources</h3><span className="hint">Choose one backup for every shard, or choose a source backup per shard.</span></div>
          {planError && <span className="field-error">Shard backup choices could not be loaded.</span>}
          {!planTable && !planError && (planLoading ? <Empty text="Shard backup choices are loading." /> : table?.shards.length ? <div className="shard-source-table-wrap"><table className="shard-source-table"><thead><tr><th>Shard</th><th>Backup</th><th>Status</th></tr></thead><tbody>{table.shards.map((shard) => <tr key={shard.id}><td>{shard.sourceShardName ? `${shard.sourceShardNumber} (${shard.sourceShardName})` : shard.sourceShardNumber}</td><td>{table.effectiveBackupType} · selected anchor backup</td><td>{shard.status}</td></tr>)}</tbody></table></div> : <Empty text="No shard backup choices are available for this table." />)}
          {planTable && planShards.length === 0 && <Empty text="No shard backup choices are available for this table." />}
          {planTable && planShards.length > 0 && <>
            <label className="restore-date-picker">Backup for all shards<select defaultValue="" onChange={(event) => setOneBackupForAll(event.target.value)}><option value="">Use current defaults</option>{[...new Map(planTable.candidates.filter((candidate) => candidate.isCompatible).map((candidate) => [candidate.backupId, candidate])).values()].map((candidate) => <option key={candidate.backupId} value={candidate.backupId}>{candidate.backupType} · {candidate.backupStatus} · {formatTime(candidate.createdAt)}</option>)}</select></label>
            <div className="shard-source-table-wrap">
              <table className="shard-source-table">
                <thead><tr><th>Shard</th><th>Backup</th><th>Status</th></tr></thead>
                <tbody>{planShards.map((shard) => {
                  const candidates = candidatesByShard.get(shard.sourceShardNumber) ?? [];
                  const selectedSource = selectedSourceForShard(shard.sourceShardNumber);
                  return <tr key={shard.sourceShardNumber}>
                    <td>{shard.sourceShardName ? `${shard.sourceShardNumber} (${shard.sourceShardName})` : shard.sourceShardNumber}</td>
                    <td><select value={selectedSource} onChange={(event) => setSourceForShard(shard.sourceShardNumber, event.target.value)}>{candidates.map((candidate) => <option key={candidate.backupTableShardId} value={candidate.backupTableShardId} disabled={!candidate.isCompatible}>{candidate.backupType} · {candidate.backupStatus} · {formatTime(candidate.createdAt)}{candidate.isDefault ? " · default" : ""}{candidate.unavailableReason ? ` · ${candidate.unavailableReason}` : ""}</option>)}</select></td>
                    <td>{candidates.find((candidate) => candidate.backupTableShardId === selectedSource)?.isCompatible === false ? "Unavailable" : "Ready"}</td>
                  </tr>;
                })}</tbody>
              </table>
            </div>
          </>}
        </div>}
        <label className="checkbox-row restore-advanced-toggle"><input type="checkbox" checked={enabled} disabled={schemaLoading} onChange={(event) => setOverride(event.target.checked)} /> Override CREATE TABLE statement</label>
        {enabled && <textarea className="create-table-override-editor" aria-label={`Custom CREATE TABLE SQL for ${sourceName}`} value={sql} disabled={schemaLoading} onChange={(event) => setSql(event.target.value)} />}
        {!canSave && <span className="field-error">Custom CREATE TABLE SQL must not be empty.</span>}
        <div className="confirm-actions">
          <button type="button" className="ghost" onClick={onClose}>Cancel</button>
          <button type="button" className="primary" disabled={!canSave || schemaLoading} onClick={() => onSave({ createTableSqlOverride: enabled ? sql : null, shardSources })}>Apply</button>
        </div>
      </div>
    </section>
  </div>;
}
function isDistributedEngine(engine: string) {
  return /^Distributed\b/i.test(engine.trim());
}
function SourceShardsPicker({ shards, selected, onChange }: { shards: SourceShardOption[]; selected: number[]; onChange: (value: number[]) => void }) {
  if (shards.length === 0) return <Empty text="This backup does not expose source shard choices." />;
  const setShard = (value: number, checked: boolean) => {
    const next = checked ? [...new Set([...selected, value])] : selected.filter((item) => item !== value);
    onChange(next.sort((a, b) => a - b));
  };
  return (
    <div className="restore-shards">
      <div className="section-head">
        <div>
          <h3>Source shards</h3>
          <span className="hint">All shards are selected by default. Clear a shard only when you intentionally want to restore a partial source.</span>
        </div>
        <div className="actions">
          <button type="button" className="secondary" onClick={() => onChange(shards.map((shard) => shard.value))}>Check all</button>
          <button type="button" className="ghost" onClick={() => onChange([])}>Clear</button>
        </div>
      </div>
      <div className="restore-shard-list">
        {shards.map((shard) => (
          <label className="checkbox-row shard-option" key={shard.value}>
            <input type="checkbox" checked={selected.includes(shard.value)} onChange={(event) => setShard(shard.value, event.target.checked)} />
            {shard.label}
          </label>
        ))}
      </div>
    </div>
  );
}
function TargetShardsPicker({ shards, selected, onChange, isLoading }: { shards: TargetShardOption[]; selected: number[]; onChange: (value: number[]) => void; isLoading: boolean }) {
  if (isLoading) return <Empty text="Loading target shard topology..." />;
  if (shards.length === 0) return <Empty text="Choose a target cluster so Chobo can load its target shards." />;
  const setShard = (value: number, checked: boolean) => {
    const next = checked ? [...new Set([...selected, value])] : selected.filter((item) => item !== value);
    onChange(next.sort((a, b) => a - b));
  };
  return (
    <div className="restore-shards">
      <div className="section-head">
        <div>
          <h3>Target shards for redistribute</h3>
          <span className="hint">Redistribute uses only the checked target shards. Select one shard when you want all restored data placed there.</span>
        </div>
        <div className="actions">
          <button type="button" className="secondary" onClick={() => onChange(shards.map((shard) => shard.value))}>Check all</button>
          <button type="button" className="ghost" onClick={() => onChange([])}>Clear</button>
        </div>
      </div>
      <div className="restore-shard-list">
        {shards.map((shard) => (
          <label className="checkbox-row shard-option" key={shard.value}>
            <input type="checkbox" checked={selected.includes(shard.value)} onChange={(event) => setShard(shard.value, event.target.checked)} />
            {shard.label}
          </label>
        ))}
      </div>
    </div>
  );
}
