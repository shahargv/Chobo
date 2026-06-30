import { useEffect, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate, useParams } from "react-router-dom";
import { CalendarClock, Copy, ListFilter, Play, Save, X } from "lucide-react";
import type { BackupContentMode, BackupPolicyDto, BackupType, FailedBackupRetentionMode, PolicyMatchKind, PolicySelector, PolicySelectorAction, PolicySelectorRule, UpsertPolicyRequest } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Input, Page, Select } from "../components/ui";
import { ClickHouseAdvancedSettingsEditor, type ClickHouseSettings } from "../components/ClickHouseAdvancedSettingsEditor";
import { emptySelector } from "../policies";
import { move } from "../utils/arrays";
import { nameOf } from "../utils/format";
export function Policies() {
  const { api, showToast } = useApi();
  const navigate = useNavigate();
  const { policyId } = useParams();
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<BackupPolicyDto | null>(null);
  const [createdPolicy, setCreatedPolicy] = useState<BackupPolicyDto | null>(null);
  const [runPolicyTarget, setRunPolicyTarget] = useState<BackupPolicyDto | null>(null);
  const defaultPolicyDraft = (): UpsertPolicyRequest => ({ name: "", sourceClusterId: "", targetId: "", selector: emptySelector, contentMode: "SchemaAndData", retention: { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }, maxAgeHoursForBaseBackup: null, failedBackupRetentionMode: "KeepAndExcludeFromMinBackupsToKeep", clickHouseBackupSettings: {}, clickHouseRestoreSettings: {} });
  const [draft, setDraft] = useState<UpsertPolicyRequest>(() => defaultPolicyDraft());
  const editPolicy = (policy: BackupPolicyDto) => {
    setEditing(policy);
    setCreatedPolicy(null);
    setDraft({ name: policy.name, sourceClusterId: policy.sourceClusterId, targetId: policy.targetId ?? "", selector: policy.selector, contentMode: policy.contentMode, retention: policy.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }, maxAgeHoursForBaseBackup: policy.maxAgeHoursForBaseBackup ?? null, failedBackupRetentionMode: policy.failedBackupRetentionMode, clickHouseBackupSettings: policy.clickHouseBackupSettings ?? {}, clickHouseRestoreSettings: policy.clickHouseRestoreSettings ?? {} });
    setShowForm(true);
  };
  useEffect(() => {
    if (!policyId || !policies.data || editing?.id === policyId) return;
    const policy = policies.data.find((item) => item.id === policyId);
    if (policy) editPolicy(policy);
  }, [policyId, policies.data, editing?.id]);
  const policyErrors = validatePolicyDraft(draft);
  const simulationSourceClusterId = useDebouncedValue(draft.sourceClusterId, 350);
  const simulationSelector = useDebouncedValue(draft.selector, 350);
  const simulation = useQuery({
    queryKey: ["policy-simulation", simulationSourceClusterId, simulationSelector],
    queryFn: () => api.simulatePolicy({ sourceClusterId: simulationSourceClusterId, selector: simulationSelector }),
    enabled: showForm && simulationSourceClusterId.length > 0,
    retry: false
  });
  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setDraft(defaultPolicyDraft());
  };
  const save = useMutation({
    mutationFn: () => editing ? api.updatePolicy(editing.id, normalizedPolicyDraft(draft)) : api.addPolicy(normalizedPolicyDraft(draft)),
    onSuccess: (policy) => {
      showToast({ kind: "success", text: "Policy saved." });
      policies.refetch();
      setCreatedPolicy(editing ? null : policy);
      reset();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const executePolicy = useMutation({
    mutationFn: ({ policy, mode, clickHouseBackupSettings }: { policy: BackupPolicyDto; mode: PolicyRunMode; clickHouseBackupSettings: ClickHouseSettings }) => api.manualBackup({
      clusterId: policy.sourceClusterId,
      targetId: policy.contentMode === "SchemaOnly" ? (null as unknown as string) : policy.targetId,
      selector: policy.selector,
      backupType: backupTypeForPolicyRun(policy, mode),
      policyId: policy.id,
      schemaOnly: policy.contentMode === "SchemaOnly",
      clickHouseBackupSettings
    }),
    onSuccess: () => {
      showToast({ kind: "success", text: "Backup queued." });
      setCreatedPolicy(null);
      setRunPolicyTarget(null);
      navigate("/queue");
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Policies" subtitle="Define which ClickHouse tables are backed up and how long backup history is retained." action={<button className="primary" onClick={() => {
      setEditing(null);
      setCreatedPolicy(null);
      setDraft({ ...defaultPolicyDraft(), sourceClusterId: clusters.data?.[0]?.id ?? "", targetId: targets.data?.[0]?.id ?? "" });
      setShowForm(true);
    }}><Save size={16} /> Add policy</button>}>
      <section className="panel">
        <DataTable headers={["Name", "Policy id", "Mode", "Source", "Backup Storage", "Rules", "Retention", "Actions"]} isLoading={policies.isLoading}>
          {(policies.data ?? []).map((policy) => (
            <tr key={policy.id} className={editing?.id === policy.id ? "editing-row" : undefined}>
              <td>{policy.name}{policy.isSystemDefault && <span className="chip">system</span>}</td>
              <td className="mono">{policy.id}</td>
              <td>{policy.contentMode === "SchemaOnly" ? "Schema only" : "Schema + data"}</td>
              <td>{nameOf(clusters.data, policy.sourceClusterId)}</td>
              <td>{policy.targetId ? nameOf(targets.data, policy.targetId) : "none"}</td>
              <td>{policy.selector.rules.length}</td>
              <td><PolicyRetentionSummary policy={policy} /></td>
              <td className="actions"><button className="ghost" onClick={() => editPolicy(policy)}>Edit</button><button className="ghost" disabled={executePolicy.isPending} onClick={() => setRunPolicyTarget(policy)}><Play size={14} /> Run now</button></td>
            </tr>
          ))}
        </DataTable>
      </section>
      {createdPolicy && <section className="panel followup-panel">
        <div className="section-head">
          <div>
            <h2>Policy created</h2>
            <p>Do you want to schedule this policy or run the first backup now?</p>
          </div>
          <button className="ghost" onClick={() => setCreatedPolicy(null)}>Dismiss</button>
        </div>
        <div className="actions">
          <button className="primary" onClick={() => navigate("/schedules", { state: { policyId: createdPolicy.id } })}><CalendarClock size={16} /> Add schedule</button>
          <button className="secondary" disabled={executePolicy.isPending} onClick={() => setRunPolicyTarget(createdPolicy)}><Play size={16} /> Run backup now</button>
        </div>
      </section>}
      {runPolicyTarget && <RunPolicyDialog policy={runPolicyTarget} busy={executePolicy.isPending} onCancel={() => setRunPolicyTarget(null)} onRun={(mode, clickHouseBackupSettings) => executePolicy.mutate({ policy: runPolicyTarget, mode, clickHouseBackupSettings })} />}
      {showForm && <section className="panel form-panel policy-form-panel">
        <div className="section-head"><h2>{editing ? "Edit policy" : "Create policy"}</h2><button className="ghost" onClick={reset}>Cancel</button></div>
        <div className="form-grid policy-general-grid">
          <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
          <Select label="Source cluster" value={draft.sourceClusterId} onChange={(value) => setDraft({ ...draft, sourceClusterId: value })} options={(clusters.data ?? []).map((cluster) => [cluster.id, cluster.name])} />
          <Select label="Backup mode" value={draft.contentMode} onChange={(value) => setDraft({ ...draft, contentMode: value as BackupContentMode, targetId: value === "SchemaOnly" ? "" : draft.targetId })} options={[["SchemaAndData", "Schema + data"], ["SchemaOnly", "Schema only"]]} />
          {draft.contentMode === "SchemaAndData" && <Select label="Backup storage" value={draft.targetId} onChange={(value) => setDraft({ ...draft, targetId: value })} options={(targets.data ?? []).map((target) => [target.id, target.name])} />}
          <Select label="Failed backups" value={draft.failedBackupRetentionMode} onChange={(value) => setDraft({ ...draft, failedBackupRetentionMode: value as FailedBackupRetentionMode })} options={[["KeepAndExcludeFromMinBackupsToKeep", "Keep"], ["DeleteByGarbageCollectorAfterFailure", "Garbage collect failed backups"]]} />
        </div>
        <div className="policy-form-section policy-selector-section">
          <SelectorBuilder selector={draft.selector} hasSource={draft.sourceClusterId.length > 0} inventory={simulation.data?.inventory ?? []} selected={simulation.data?.tables ?? []} isLoading={simulation.isFetching} error={simulation.error ? String(simulation.error) : null} onChange={(selector) => setDraft({ ...draft, selector })} />
        </div>
        <div className="policy-form-section policy-retention-section">
          <RetentionEditor value={draft.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }} maxAgeHoursForBaseBackup={draft.maxAgeHoursForBaseBackup ?? null} onChange={(retention) => setDraft({ ...draft, retention })} onMaxAgeChange={(maxAgeHoursForBaseBackup) => setDraft({ ...draft, maxAgeHoursForBaseBackup })} />
        </div>
        <div className="policy-form-section policy-advanced-section">
          <ClickHouseAdvancedSettingsEditor title="Backup advanced settings" value={(draft.clickHouseBackupSettings ?? {}) as Record<string, string | number | boolean>} onChange={(settings) => setDraft({ ...draft, clickHouseBackupSettings: settings })} />
          <ClickHouseAdvancedSettingsEditor title="Default restore advanced settings" value={(draft.clickHouseRestoreSettings ?? {}) as Record<string, string | number | boolean>} onChange={(settings) => setDraft({ ...draft, clickHouseRestoreSettings: settings })} />
        </div>
        {policyErrors.map((error) => <span className="field-error" key={error}>{error}</span>)}
        <button className="primary" disabled={policyErrors.length > 0 || save.isPending} onClick={() => {
          if (policyErrors.length === 0) save.mutate();
        }}><Save size={16} /> Save policy</button>
      </section>}
    </Page>
  );
}


export function formatPolicyRetentionSummary(policy: Pick<BackupPolicyDto, "retention" | "maxAgeHoursForBaseBackup">): string[] {
  const retention = policy.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 };
  const lines = [
    `Min backups to keep: full - ${formatLimitedNumber(retention.minFullBackupsToKeep)}, any - ${formatLimitedNumber(retention.minBackupsToKeep)}`,
    `Retention time: full - ${formatRetentionMinutes(retention.fullRetentionMinutes)}, any - ${formatRetentionMinutes(retention.incrementalRetentionMinutes)}`
  ];
  if (policy.maxAgeHoursForBaseBackup != null) lines.push(`New full backup every ${policy.maxAgeHoursForBaseBackup} hours`);
  return lines;
}

function PolicyRetentionSummary({ policy }: { policy: BackupPolicyDto }) {
  return <ul className="policy-retention-summary">
    {formatPolicyRetentionSummary(policy).map((line) => <li key={line}>{line}</li>)}
  </ul>;
}

function formatLimitedNumber(value: number | null | undefined) {
  return value && value > 0 ? `${value}` : "unlimited";
}

function formatRetentionMinutes(value: number | null | undefined) {
  return value && value > 0 ? `${value} minutes` : "unlimited";
}
type PolicyRunMode = "full" | "regular";

function RunPolicyDialog({ policy, busy, onRun, onCancel }: { policy: BackupPolicyDto; busy: boolean; onRun: (mode: PolicyRunMode, clickHouseBackupSettings: ClickHouseSettings) => void; onCancel: () => void }) {
  const { api } = useApi();
  const [clickHouseSettings, setClickHouseSettings] = useState<ClickHouseSettings>({});
  const [settingsValid, setSettingsValid] = useState(true);
  const regularDisabled = policy.contentMode === "SchemaOnly";
  const fullDescription = regularDisabled ? "Capture the selected schema as a new full schema snapshot." : "Capture all selected table data and schema as a new full base backup.";
  const settingsPreview = useQuery({
    queryKey: ["backup-settings-preview", policy.sourceClusterId, policy.id],
    queryFn: () => api.backupSettingsPreview({ clusterId: policy.sourceClusterId, policyId: policy.id }),
    enabled: Boolean(policy.sourceClusterId && policy.id)
  });

  useEffect(() => {
    setClickHouseSettings((settingsPreview.data?.settings ?? {}) as ClickHouseSettings);
  }, [settingsPreview.data, policy.id]);

  return <div className="modal-backdrop" role="presentation" onClick={onCancel}>
    <section className="confirm-dialog run-policy-dialog" role="dialog" aria-modal="true" aria-labelledby="run-policy-dialog-title" onClick={(event) => event.stopPropagation()}>
      <div className="confirm-icon primary"><Play size={22} /></div>
      <div className="confirm-content">
        <h2 id="run-policy-dialog-title">Run policy now</h2>
        <p>Choose how to run {policy.name}.</p>
        <ClickHouseAdvancedSettingsEditor title="ClickHouse backup settings for this run" value={clickHouseSettings} sources={(settingsPreview.data?.sources ?? []) as any} onChange={setClickHouseSettings} onValidityChange={setSettingsValid} />
        {settingsPreview.error && <span className="field-error">{String(settingsPreview.error)}</span>}
        <div className="run-policy-options">
          <button className="secondary run-policy-option" disabled={busy || !settingsValid || settingsPreview.isLoading || settingsPreview.isError} onClick={() => onRun("full", clickHouseSettings)}>
            <strong>Full backup</strong>
            <span>{fullDescription}</span>
          </button>
          <button className="secondary run-policy-option" disabled={busy || regularDisabled || !settingsValid || settingsPreview.isLoading || settingsPreview.isError} onClick={() => onRun("regular", clickHouseSettings)}>
            <strong>Regular backup</strong>
            <span>Back up only changes since the latest usable full backup. If no suitable full backup is available, Chobo automatically creates a full backup for the affected data.</span>
          </button>
        </div>
        {regularDisabled && <span className="hint">Schema-only policies always run as full schema captures.</span>}
        <div className="confirm-actions"><button className="ghost" disabled={busy} onClick={onCancel}>Cancel</button></div>
      </div>
    </section>
  </div>;
}
export function backupTypeForPolicyRun(policy: Pick<BackupPolicyDto, "contentMode">, mode: PolicyRunMode): BackupType {
  return mode === "regular" && policy.contentMode !== "SchemaOnly" ? "Incremental" : "Full";
}

function SelectorBuilder({ selector, hasSource, inventory, selected, isLoading, error, onChange }: { selector: PolicySelector; hasSource: boolean; inventory: Array<{ database: string; table: string }>; selected: Array<{ database: string; table: string }>; isLoading: boolean; error: string | null; onChange: (selector: PolicySelector) => void }) {
  const updateRule = (index: number, rule: PolicySelectorRule) => onChange({ ...selector, rules: selector.rules.map((item, i) => i === index ? rule : item) });
  const duplicateRule = (index: number) => onChange({ ...selector, rules: [...selector.rules.slice(0, index + 1), copyRule(selector.rules[index]), ...selector.rules.slice(index + 1)] });
  const excludeTable = (table: { database: string; table: string }) => onChange({ ...selector, rules: [...selector.rules, excludeTableRule(table)] });
  return (
    <div className="selector-builder">
      <div className="section-head">
        <h3>Selector rules</h3>
        <button className="secondary" onClick={() => onChange({ ...selector, rules: [...selector.rules, { action: "Exclude", database: { kind: "Exact", value: "" }, table: { kind: "All", value: "*" } }] })}><ListFilter size={16} /> Add rule</button>
      </div>
      {selector.rules.map((rule, index) => (
        <div className="rule-row" key={index}>
          <span className="drag-handle">{index + 1}</span>
          <select value={rule.action} onChange={(event) => updateRule(index, { ...rule, action: event.target.value as PolicySelectorAction })}>
            <option value="Include">Include</option>
            <option value="Exclude">Exclude</option>
          </select>
          <PatternEditor label="Database" pattern={rule.database} onChange={(database) => updateRule(index, { ...rule, database })} />
          <PatternEditor label="Table" pattern={rule.table} onChange={(table) => updateRule(index, { ...rule, table })} />
          <button className="ghost" onClick={() => index > 0 && onChange({ ...selector, rules: move(selector.rules, index, index - 1) })}>Up</button>
          <button className="ghost" onClick={() => duplicateRule(index)}><Copy size={14} /> Duplicate</button>
          <button className="ghost" onClick={() => onChange({ ...selector, rules: selector.rules.filter((_, i) => i !== index) })}>Remove</button>
        </div>
      ))}
      <div className="preview-grid">
        <pre>{JSON.stringify(selector, null, 2)}</pre>
        <div>
          {!hasSource && <span className="hint">Choose a source cluster to preview the selected tables.</span>}
          {hasSource && isLoading && <span className="hint">Loading tables from ClickHouse...</span>}
          {hasSource && !isLoading && error && <span className="field-error">{error}</span>}
          {hasSource && !isLoading && !error && inventory.length === 0 && <span className="hint">No tables were returned by ClickHouse for this cluster.</span>}
          {hasSource && !isLoading && !error && inventory.length > 0 && <SelectedTablesPreview inventoryCount={inventory.length} selected={selected} onExcludeTable={excludeTable} />}
        </div>
      </div>
    </div>
  );
}


function useDebouncedValue<T>(value: T, delayMs: number) {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timeout = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(timeout);
  }, [value, delayMs]);
  return debounced;
}
export function SelectedTablesPreview({ inventoryCount, selected, onExcludeTable }: { inventoryCount: number; selected: Array<{ database: string; table: string }>; onExcludeTable?: (table: { database: string; table: string }) => void }) {
  const [showAll, setShowAll] = useState(false);
  useEffect(() => setShowAll(false), [selected]);
  const visible = showAll ? selected : selected.slice(0, 100);
  const hiddenCount = selected.length - visible.length;
  return <>
    <h4>Tables selected</h4>
    <span className="hint">{selected.length} of {inventoryCount} table(s) will be backed up.</span>
    {visible.map((table) => <span className="chip table-chip" key={`${table.database}.${table.table}`}>{table.database}.{table.table}{onExcludeTable && <button className="chip-remove" type="button" title={`Exclude ${table.database}.${table.table}`} aria-label={`Exclude ${table.database}.${table.table}`} onClick={() => onExcludeTable(table)}><X size={12} /></button>}</span>)}
    {hiddenCount > 0 && <span className="hint">Showing first {visible.length}; {hiddenCount} more matched table(s) are included. <button className="link-button" onClick={() => setShowAll(true)}>Show all</button></span>}
  </>;
}
export function copyRule(rule: PolicySelectorRule): PolicySelectorRule {
  return { action: rule.action, database: { ...rule.database }, table: { ...rule.table } };
}

export function excludeTableRule(table: { database: string; table: string }): PolicySelectorRule {
  return { action: "Exclude", database: { kind: "Exact", value: table.database }, table: { kind: "Exact", value: table.table } };
}

function PatternEditor({ label, pattern, onChange }: { label: string; pattern: { kind: PolicyMatchKind; value: string }; onChange: (pattern: { kind: PolicyMatchKind; value: string }) => void }) {
  return (
    <div className="pattern-editor">
      <span>{label}</span>
      <select value={pattern.kind} onChange={(event) => onChange({ kind: event.target.value as PolicyMatchKind, value: event.target.value === "All" ? "*" : pattern.value })}>
        <option value="All">all</option>
        <option value="Exact">exact</option>
        <option value="Wildcard">wildcard</option>
      </select>
      <input value={pattern.value} disabled={pattern.kind === "All"} onChange={(event) => onChange({ ...pattern, value: event.target.value })} />
    </div>
  );
}

function RetentionEditor({ value, maxAgeHoursForBaseBackup, onChange, onMaxAgeChange }: { value: NonNullable<UpsertPolicyRequest["retention"]>; maxAgeHoursForBaseBackup: number | null; onChange: (value: NonNullable<UpsertPolicyRequest["retention"]>) => void; onMaxAgeChange: (value: number | null) => void }) {
  const retention = value;
  return (
    <div className="retention-editor">
      <h3>Retention</h3>
      <span className="hint">Leave a retention minutes field empty for no time-based retention limit. Minimum counts of 0 mean no minimum backup count is protected.</span>
      <div className="form-grid policy-general-grid">
        <Input label="Full retention minutes (empty = no retention)" type="number" value={retention.fullRetentionMinutes?.toString() ?? ""} onChange={(value) => onChange({ ...retention, fullRetentionMinutes: value ? Number(value) : null })} />
        <Input label="Incremental retention minutes (empty = no retention)" type="number" value={retention.incrementalRetentionMinutes?.toString() ?? ""} onChange={(value) => onChange({ ...retention, incrementalRetentionMinutes: value ? Number(value) : null })} />
        <Input label="Min backups to keep" type="number" value={`${retention.minBackupsToKeep}`} onChange={(value) => onChange({ ...retention, minBackupsToKeep: Number(value) || 0 })} />
        <Input label="Min full backups" type="number" value={`${retention.minFullBackupsToKeep}`} onChange={(value) => onChange({ ...retention, minFullBackupsToKeep: Number(value) || 0 })} />
        <Input label="Max base backup age hours (empty = app default)" type="number" value={maxAgeHoursForBaseBackup?.toString() ?? ""} onChange={(value) => onMaxAgeChange(value ? Number(value) : null)} />
      </div>
    </div>
  );
}

function normalizedPolicyDraft(draft: UpsertPolicyRequest): UpsertPolicyRequest {
  return {
    ...draft,
    targetId: draft.contentMode === "SchemaOnly" ? (null as unknown as string) : draft.targetId
  };
}
function validatePolicyDraft(draft: UpsertPolicyRequest) {
  const errors: string[] = [];
  if (!draft.name.trim()) errors.push("Policy name is required.");
  if (!draft.sourceClusterId) errors.push("Choose a source cluster.");
  if (draft.contentMode === "SchemaAndData" && !draft.targetId) errors.push("Choose backup storage.");
  if (draft.selector.rules.length === 0) errors.push("Add at least one selector rule.");
  if (draft.maxAgeHoursForBaseBackup != null && draft.maxAgeHoursForBaseBackup <= 0) errors.push("Max base backup age hours must be greater than zero.");
  return errors;
}

