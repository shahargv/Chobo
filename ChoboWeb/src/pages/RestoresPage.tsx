import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { RotateCcw } from "lucide-react";
import type { BackupDto, InitiateRestoreRequest, RestoreLayout, RestoreTableMappingRequest } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Empty, Input, Page, Select, Status } from "../components/ui";
import { formatTime } from "../utils/format";

type RestoreMappingDraft = RestoreTableMappingRequest & { selected: boolean };
type SourceShardOption = { value: number; label: string };
export function Restores() {
  const { api, showToast } = useApi();
  const restores = useQuery({ queryKey: ["restores"], queryFn: () => api.restores() });
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const [request, setRequest] = useState<InitiateRestoreRequest>({ backupId: "", targetClusterId: "", append: false, allowSchemaMismatch: false, layout: "Preserve", schemaOnly: false });
  const [mappings, setMappings] = useState<RestoreMappingDraft[]>([]);
  const [selectedSourceShards, setSelectedSourceShards] = useState<number[]>([]);
  const selectedBackup = (backups.data ?? []).find((backup) => backup.id === request.backupId) ?? null;
  const sourceShardOptions = useMemo(() => getSourceShardOptions(selectedBackup), [selectedBackup]);
  useEffect(() => {
    if (!selectedBackup) {
      setMappings([]);
      setSelectedSourceShards([]);
      return;
    }

    setMappings(selectedBackup.tables.map((table) => ({
      backupTableId: table.id,
      targetDatabase: table.database,
      targetTable: restoreTargetTableName(table.table),
      append: false,
      allowSchemaMismatch: false,
      schemaOnly: !table.dataBackedUp,
      selected: false
    })));
    setSelectedSourceShards(getSourceShardOptions(selectedBackup).map((shard) => shard.value));
  }, [selectedBackup?.id]);
  const restoreRequest = (): InitiateRestoreRequest => ({
    ...request,
    sourceShard: null,
    sourceShards: selectedSourceShards.length === 0 || selectedSourceShards.length === sourceShardOptions.length ? null : selectedSourceShards,
    tables: mappings
      .filter((mapping) => mapping.selected)
      .map(({ selected: _, ...mapping }) => ({
        ...mapping,
        targetDatabase: mapping.targetDatabase || null,
        targetTable: mapping.targetTable ? restoreTargetTableName(mapping.targetTable) : null,
        append: mapping.schemaOnly ? false : mapping.append ?? false,
        allowSchemaMismatch: mapping.allowSchemaMismatch ?? false,
        schemaOnly: mapping.schemaOnly ?? false
      }))
  });
  const restoreErrors = validateRestoreRequest(request, mappings, selectedSourceShards, sourceShardOptions.length);
  const mutation = useMutation({
    mutationFn: () => api.initiateRestore(restoreRequest()),
    onSuccess: () => {
      showToast({ kind: "success", text: "Restore queued." });
      restores.refetch();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Restores">
      <section className="panel form-panel">
        <h2>Start restore</h2>
        <div className="form-grid">
          <Select label="Backup" value={request.backupId} onChange={(value) => setRequest({ ...request, backupId: value })} options={(backups.data ?? []).map((b) => [b.id, `${b.backupType} · ${formatTime(b.createdAt)}`])} />
          <Select label="Target cluster" value={request.targetClusterId} onChange={(value) => setRequest({ ...request, targetClusterId: value })} options={(clusters.data ?? []).map((c) => [c.id, c.name])} />
          <Select label="Layout" value={request.layout ?? "Preserve"} onChange={(value) => setRequest({ ...request, layout: value as RestoreLayout })} options={[["Preserve", "Preserve"], ["Redistribute", "Redistribute"], ["SingleNode", "Single node"]]} />
        </div>
        <SourceShardsPicker shards={sourceShardOptions} selected={selectedSourceShards} onChange={setSelectedSourceShards} />
        <RestoreMappingsEditor backup={selectedBackup} mappings={mappings} onChange={setMappings} />
        {request.schemaOnly && <span className="hint">Schema-only restore creates the target tables without restoring data from backup storage.</span>}
        {restoreErrors.map((error) => <span className="field-error" key={error}>{error}</span>)}
        <button className="primary" disabled={restoreErrors.length > 0 || mutation.isPending} onClick={() => mutation.mutate()}><RotateCcw size={16} /> Queue restore</button>
      </section>
      <section className="panel">
        <DataTable headers={["Status", "Backup", "Target", "Layout", "Started", "Failure"]}>
          {(restores.data ?? []).map((restore) => (
            <tr key={restore.id}>
              <td><Status value={restore.status} /></td>
              <td>{restore.backupId}</td>
              <td>{restore.targetClusterId}</td>
              <td>{restore.layout}</td>
              <td>{formatTime(restore.startedAt)}</td>
              <td>{restore.failureReason ?? ""}</td>
            </tr>
          ))}
        </DataTable>
      </section>
    </Page>
  );
}

function RestoreMappingsEditor({ backup, mappings, onChange }: { backup: BackupDto | null; mappings: RestoreMappingDraft[]; onChange: (mappings: RestoreMappingDraft[]) => void }) {
  if (!backup) return <Empty text="Choose a backup to select tables." />;
  if (backup.tables.length === 0) return <Empty text="This backup does not contain restorable tables." />;
  const update = (id: string, patch: Partial<RestoreMappingDraft>) => onChange(mappings.map((mapping) => mapping.backupTableId === id ? { ...mapping, ...patch } : mapping));
  const mapped = (tableId: string, database: string, table: string) =>
    mappings.find((item) => item.backupTableId === tableId) ?? { backupTableId: tableId, targetDatabase: database, targetTable: restoreTargetTableName(table), append: false, allowSchemaMismatch: false, schemaOnly: false, selected: false };
  return (
    <div className="restore-mapping-editor">
      <div className="section-head">
        <div>
          <h3>Table mappings</h3>
          <span className="hint">Select source tables and choose where each one should be restored. Target tables use the _restore suffix.</span>
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
            <td>{table.database}.{table.table}</td>
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
                <label className="checkbox-row"><input type="checkbox" checked={mapping.append ?? false} disabled={!mapping.selected || mapping.schemaOnly || !table.dataBackedUp} onChange={(event) => update(table.id, { append: event.target.checked })} /> Append</label>
                <label className="checkbox-row"><input type="checkbox" checked={mapping.allowSchemaMismatch ?? false} disabled={!mapping.selected} onChange={(event) => update(table.id, { allowSchemaMismatch: event.target.checked })} /> Allow schema mismatch</label>
              </div>
            </td>
          </tr>;
        })}
      </DataTable>
    </div>
  );
}

function SourceShardsPicker({ shards, selected, onChange }: { shards: SourceShardOption[]; selected: number[]; onChange: (value: number[]) => void }) {
  if (shards.length === 0) return <Empty text="Choose a backup to select source shards." />;
  const setShard = (value: number, checked: boolean) => {
    const next = checked ? [...new Set([...selected, value])] : selected.filter((item) => item !== value);
    onChange(next.sort((a, b) => a - b));
  };
  return (
    <div className="restore-shards">
      <div className="section-head">
        <div>
          <h3>Source shards</h3>
          <span className="hint">All shards are selected by default. Uncheck shards you want to skip.</span>
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
function validateRestoreRequest(request: InitiateRestoreRequest, mappings: RestoreMappingDraft[], selectedSourceShards: number[], sourceShardCount: number) {
  const errors: string[] = [];
  if (!request.backupId) errors.push("Choose a backup.");
  if (!request.targetClusterId) errors.push("Choose a target ClickHouse cluster.");
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
  });
  return [...new Set(errors)];
}

function getSourceShardOptions(backup: BackupDto | null): SourceShardOption[] {
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

function restoreTargetTableName(table: string) {
  return table.endsWith("_restore") ? table : `${table}_restore`;
}

