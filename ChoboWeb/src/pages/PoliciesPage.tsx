import { useEffect, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate, useParams } from "react-router-dom";
import { CalendarClock, ListFilter, Play, Save } from "lucide-react";
import type { BackupContentMode, BackupPolicyDto, FailedBackupRetentionMode, PolicyMatchKind, PolicySelector, PolicySelectorAction, PolicySelectorRule, UpsertPolicyRequest } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Input, Page, Select } from "../components/ui";
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
  const defaultPolicyDraft = (): UpsertPolicyRequest => ({ name: "", sourceClusterId: "", targetId: "", selector: emptySelector, contentMode: "SchemaAndData", retention: { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }, failedBackupRetentionMode: "KeepAndExcludeFromMinBackupsToKeep" });
  const [draft, setDraft] = useState<UpsertPolicyRequest>(() => defaultPolicyDraft());
  const editPolicy = (policy: BackupPolicyDto) => {
    setEditing(policy);
    setCreatedPolicy(null);
    setDraft({ name: policy.name, sourceClusterId: policy.sourceClusterId, targetId: policy.targetId ?? "", selector: policy.selector, contentMode: policy.contentMode, retention: policy.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }, failedBackupRetentionMode: policy.failedBackupRetentionMode });
    setShowForm(true);
  };
  useEffect(() => {
    if (!policyId || !policies.data) return;
    const policy = policies.data.find((item) => item.id === policyId);
    if (policy) editPolicy(policy);
  }, [policyId, policies.data]);
  const policyErrors = validatePolicyDraft(draft);
  const simulation = useQuery({
    queryKey: ["policy-simulation", draft.sourceClusterId, draft.selector],
    queryFn: () => api.simulatePolicy({ sourceClusterId: draft.sourceClusterId, selector: draft.selector }),
    enabled: showForm && draft.sourceClusterId.length > 0,
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
    mutationFn: (policy: BackupPolicyDto) => api.manualBackup({
      clusterId: policy.sourceClusterId,
      targetId: policy.contentMode === "SchemaOnly" ? (null as unknown as string) : policy.targetId,
      selector: policy.selector,
      backupType: "Full",
      policyId: policy.id,
      schemaOnly: policy.contentMode === "SchemaOnly"
    }),
    onSuccess: () => {
      showToast({ kind: "success", text: "Backup queued." });
      setCreatedPolicy(null);
      navigate("/backups");
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
        <DataTable headers={["Name", "Mode", "Source", "Backup Storage", "Rules", "Retention", "Actions"]}>
          {(policies.data ?? []).map((policy) => (
            <tr key={policy.id}>
              <td>{policy.name}{policy.isSystemDefault && <span className="chip">system</span>}</td>
              <td>{policy.contentMode === "SchemaOnly" ? "Schema only" : "Schema + data"}</td>
              <td>{nameOf(clusters.data, policy.sourceClusterId)}</td>
              <td>{policy.targetId ? nameOf(targets.data, policy.targetId) : "none"}</td>
              <td>{policy.selector.rules.length}</td>
              <td>{policy.retention ? `${policy.retention.minBackupsToKeep} backups` : "default"}</td>
              <td className="actions"><button className="ghost" onClick={() => editPolicy(policy)}>Edit</button><button className="ghost" disabled={executePolicy.isPending} onClick={() => executePolicy.mutate(policy)}><Play size={14} /> Execute now</button></td>
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
          <button className="secondary" disabled={executePolicy.isPending} onClick={() => executePolicy.mutate(createdPolicy)}><Play size={16} /> Execute backup now</button>
        </div>
      </section>}
      {showForm && <section className="panel form-panel">
        <div className="section-head"><h2>{editing ? "Edit policy" : "Create policy"}</h2><button className="ghost" onClick={reset}>Cancel</button></div>
        <div className="form-grid">
          <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
          <Select label="Source cluster" value={draft.sourceClusterId} onChange={(value) => setDraft({ ...draft, sourceClusterId: value })} options={(clusters.data ?? []).map((cluster) => [cluster.id, cluster.name])} />
          <Select label="Backup mode" value={draft.contentMode} onChange={(value) => setDraft({ ...draft, contentMode: value as BackupContentMode, targetId: value === "SchemaOnly" ? "" : draft.targetId })} options={[["SchemaAndData", "Schema + data"], ["SchemaOnly", "Schema only"]]} />
          {draft.contentMode === "SchemaAndData" && <Select label="Backup storage" value={draft.targetId} onChange={(value) => setDraft({ ...draft, targetId: value })} options={(targets.data ?? []).map((target) => [target.id, target.name])} />}
          <Select label="Failed backups" value={draft.failedBackupRetentionMode} onChange={(value) => setDraft({ ...draft, failedBackupRetentionMode: value as FailedBackupRetentionMode })} options={[["KeepAndExcludeFromMinBackupsToKeep", "Keep"], ["DeleteByGarbageCollectorAfterFailure", "Garbage collect failed backups"]]} />
        </div>
        <SelectorBuilder selector={draft.selector} hasSource={draft.sourceClusterId.length > 0} inventory={simulation.data?.inventory ?? []} selected={simulation.data?.tables ?? []} isLoading={simulation.isFetching} error={simulation.error ? String(simulation.error) : null} onChange={(selector) => setDraft({ ...draft, selector })} />
        <RetentionEditor value={draft.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }} onChange={(retention) => setDraft({ ...draft, retention })} />
        {policyErrors.map((error) => <span className="field-error" key={error}>{error}</span>)}
        <button className="primary" disabled={policyErrors.length > 0 || save.isPending} onClick={() => {
          if (policyErrors.length === 0) save.mutate();
        }}><Save size={16} /> Save policy</button>
      </section>}
    </Page>
  );
}

function SelectorBuilder({ selector, hasSource, inventory, selected, isLoading, error, onChange }: { selector: PolicySelector; hasSource: boolean; inventory: Array<{ database: string; table: string }>; selected: Array<{ database: string; table: string }>; isLoading: boolean; error: string | null; onChange: (selector: PolicySelector) => void }) {
  const updateRule = (index: number, rule: PolicySelectorRule) => onChange({ ...selector, rules: selector.rules.map((item, i) => i === index ? rule : item) });
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
          {hasSource && !isLoading && !error && inventory.length > 0 && <>
            <h4>Tables selected</h4>
            <span className="hint">{selected.length} of {inventory.length} table(s) will be backed up.</span>
            {selected.map((table) => <span className="chip" key={`${table.database}.${table.table}`}>{table.database}.{table.table}</span>)}
          </>}
        </div>
      </div>
    </div>
  );
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

function RetentionEditor({ value, onChange }: { value: NonNullable<UpsertPolicyRequest["retention"]>; onChange: (value: NonNullable<UpsertPolicyRequest["retention"]>) => void }) {
  const retention = value;
  return (
    <div className="retention-editor">
      <h3>Retention</h3>
      <span className="hint">Leave a retention minutes field empty for no time-based retention limit. Minimum counts of 0 mean no minimum backup count is protected.</span>
      <div className="form-grid">
        <Input label="Full retention minutes (empty = no retention)" type="number" value={retention.fullRetentionMinutes?.toString() ?? ""} onChange={(value) => onChange({ ...retention, fullRetentionMinutes: value ? Number(value) : null })} />
        <Input label="Incremental retention minutes (empty = no retention)" type="number" value={retention.incrementalRetentionMinutes?.toString() ?? ""} onChange={(value) => onChange({ ...retention, incrementalRetentionMinutes: value ? Number(value) : null })} />
        <Input label="Min backups to keep" type="number" value={`${retention.minBackupsToKeep}`} onChange={(value) => onChange({ ...retention, minBackupsToKeep: Number(value) || 0 })} />
        <Input label="Min full backups" type="number" value={`${retention.minFullBackupsToKeep}`} onChange={(value) => onChange({ ...retention, minFullBackupsToKeep: Number(value) || 0 })} />
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
  return errors;
}





