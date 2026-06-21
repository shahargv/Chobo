import type { ReactNode } from "react";
import { ArrowRight, CheckCircle2, Database, GitBranch, Info, ListChecks, ShieldAlert } from "lucide-react";
import type { BackupDto, InitiateRestoreRequest, RestoreLayout } from "../../api/generated";
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

export function BackupChoiceStep({ backups, selectedBackupId, onSelect, clusterName, isLoading = false }: { backups: BackupDto[]; selectedBackupId: string; onSelect: (backupId: string) => void; clusterName: (clusterId: string) => string; isLoading?: boolean }) {
  if (!isLoading && backups.length === 0) return <Empty text="No backups are available yet. Create a backup before starting a restore." />;
  return (
    <div className="restore-step-content">
      <StepIntro icon={<Database size={20} />} title="Choose the backup to restore from" body="This sets the recovery point and the list of tables and shards you can restore. Nothing is changed until the final review step." />
      <DataTable headers={["Use", "Created", "Type", "Source cluster", "Tables", "Status"]} isLoading={isLoading}>
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
      </DataTable>
    </div>
  );
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

export function ScopeStep({ backup, mappings, onMappingsChange, sourceShardOptions, selectedSourceShards, onSourceShardsChange }: { backup: BackupDto | null; mappings: RestoreMappingDraft[]; onMappingsChange: (mappings: RestoreMappingDraft[]) => void; sourceShardOptions: SourceShardOption[]; selectedSourceShards: number[]; onSourceShardsChange: (value: number[]) => void }) {
  return (
    <div className="restore-step-content">
      <StepIntro icon={<ListChecks size={20} />} title="Choose exactly what gets restored" body="Select source shards and tables, then decide whether each table restores schema plus data or schema only. The default target table name uses _restore so existing tables are not overwritten by accident." />
      <SourceShardsPicker shards={sourceShardOptions} selected={selectedSourceShards} onChange={onSourceShardsChange} />
      <RestoreMappingsEditor backup={backup} mappings={mappings} onChange={onMappingsChange} />
    </div>
  );
}

export function ReviewStep({ backup, targetClusterName, request, mappings, sourceShardOptions, selectedSourceShards, targetShardOptions, selectedTargetShards, errors }: { backup: BackupDto | null; targetClusterName: string; request: InitiateRestoreRequest; mappings: RestoreMappingDraft[]; sourceShardOptions: SourceShardOption[]; selectedSourceShards: number[]; targetShardOptions: TargetShardOption[]; selectedTargetShards: number[]; errors: string[] }) {
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
      <DataTable headers={["Source table", "Target table", "Mode", "Append", "Schema mismatch"]}>
        {mappings.map((mapping) => {
          const table = backup?.tables.find((item) => item.id === mapping.backupTableId);
          return <tr key={mapping.backupTableId}>
            <td>{table ? `${table.database}.${table.table}` : mapping.backupTableId}</td>
            <td>{mapping.targetDatabase}.{mapping.targetTable}</td>
            <td>{mapping.schemaOnly ? "Schema only" : "Schema + data"}</td>
            <td>{mapping.schemaOnly ? "No" : mapping.append ? "Yes" : "No"}</td>
            <td>{mapping.allowSchemaMismatch ? "Allowed" : "Blocked"}</td>
          </tr>;
        })}
      </DataTable>
    </div>
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

function RestoreMappingsEditor({ backup, mappings, onChange }: { backup: BackupDto | null; mappings: RestoreMappingDraft[]; onChange: (mappings: RestoreMappingDraft[]) => void }) {
  if (!backup) return <Empty text="Choose a backup first. Its tables will appear here." />;
  if (backup.tables.length === 0) return <Empty text="This backup does not contain restorable tables." />;
  const update = (id: string, patch: Partial<RestoreMappingDraft>) => onChange(mappings.map((mapping) => mapping.backupTableId === id ? { ...mapping, ...patch } : mapping));
  const mapped = (tableId: string, database: string, table: string) =>
    mappings.find((item) => item.backupTableId === tableId) ?? { backupTableId: tableId, targetDatabase: database, targetTable: restoreTargetTableName(table), append: false, allowSchemaMismatch: false, schemaOnly: false, selected: false };
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
      <DataTable headers={["Restore", "Source table", "Target database", "Target table", "Mode", "Options"]}>
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
          </tr>;
        })}
      </DataTable>
    </div>
  );
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

