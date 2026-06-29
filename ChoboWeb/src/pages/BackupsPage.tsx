import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams } from "react-router-dom";
import { ArrowUpToLine, Ban, Info, Play, RefreshCw, RotateCcw, Trash2 } from "lucide-react";
import type { BackupDto, BackupPolicyDto, BackupRunStatus, BackupTableDto, BackupTableShardDto, BackupType } from "../api/generated";
import { useApi } from "../api-context";
import { ConfirmDialog, DataTable, Detail, Drawer, Empty, ErrorDetailDialog, ExpandableErrorText, Input, Page, Select, Status } from "../components/ui";
import { ClickHouseAdvancedSettingsEditor, type ClickHouseSettings } from "../components/ClickHouseAdvancedSettingsEditor";
import { formatBytes, formatCompletionTime, formatTime } from "../utils/format";
import { isBackupStatusRestorable } from "./restores/restoreUtils";

export function Backups() {
  const { api, showToast } = useApi();
  const { backupId } = useParams();
  const navigate = useNavigate();
  const [selectedBackupId, setSelectedBackupId] = useState<string | null>(backupId ?? null);
  const [showManual, setShowManual] = useState(false);
  const [deleteTargets, setDeleteTargets] = useState<BackupDto[] | null>(null);
  const [selectedBackupIds, setSelectedBackupIds] = useState<string[]>([]);
  const [from, setFrom] = useState(() => defaultBackupFromFilter());
  const [to, setTo] = useState("");
  const backupFilters = { from: toApiDateTime(from), to: toApiDateTime(to) };
  const backups = useQuery({ queryKey: ["backups", "summary", backupFilters], queryFn: () => api.backups(backupFilters, { includeTables: false }) });
  const schedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const backupRows = backups.data ?? [];
  const deletableBackups = backupRows.filter((backup) => !isBackupDeleted(backup));
  const selectedBackups = backupRows.filter((backup) => selectedBackupIds.includes(backup.id) && !isBackupDeleted(backup));
  const scheduleById = new Map((schedules.data ?? []).map((schedule) => [schedule.id, schedule]));
  const policyById = new Map((policies.data ?? []).map((policy) => [policy.id, policy]));
  useEffect(() => {
    setSelectedBackupId(backupId ?? null);
  }, [backupId]);
  useEffect(() => {
    const availableIds = new Set(deletableBackups.map((backup) => backup.id));
    setSelectedBackupIds((ids) => ids.filter((id) => availableIds.has(id)));
  }, [backups.data]);
  const mutation = useMutation({
    mutationFn: ({ id, action }: { id: string; action: "pin" | "unpin" | "cancel" }) =>
      action === "pin" ? api.pinBackup(id) : action === "unpin" ? api.unpinBackup(id) : api.cancelBackup(id),
    onSuccess: () => {
      backups.refetch();
      showToast({ kind: "success", text: "Backup updated." });
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const deleteMutation = useMutation({
    mutationFn: (targets: BackupDto[]) => Promise.all(targets.map((backup) => api.deleteBackup(backup.id, { force: backup.isPinned, confirmDestructive: true }))),
    onSuccess: (_result, targets) => {
      setDeleteTargets(null);
      setSelectedBackupIds((ids) => ids.filter((id) => !targets.some((backup) => backup.id === id)));
      backups.refetch();
      showToast({ kind: "success", text: targets.length === 1 ? "Backup delete requested." : `Delete requested for ${targets.length} backups.` });
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Backups" subtitle="Start manual backups, review backup history, and inspect table or table-shard results." action={<button className="primary" onClick={() => setShowManual(!showManual)}><Play size={16} /> Manual backup</button>}>
      {showManual && <ManualBackupPanel policies={policies.data ?? []} onQueued={() => { setShowManual(false); backups.refetch(); }} />}
      <section className="panel">
        <div className="section-head backup-history-filter">
          <div>
            <h2>Backup history</h2>
            <span className="hint">Showing backups created in the selected time window.</span>
          </div>
          <div className="time-filter-controls">
            <Input label="From" type="datetime-local" value={from} onChange={setFrom} />
            <Input label="To" type="datetime-local" value={to} onChange={setTo} />
            <button className="ghost" onClick={() => { setFrom(defaultBackupFromFilter()); setTo(""); }}>Last 2 weeks</button>
            <button className="ghost" onClick={() => { setFrom(""); setTo(""); }}>All time</button>
          </div>
        </div>
        <div className="bulk-actions">
          <span>{selectedBackups.length} selected</span>
          <button className="secondary" disabled={deletableBackups.length === 0 || selectedBackups.length === deletableBackups.length} onClick={() => setSelectedBackupIds(deletableBackups.map((backup) => backup.id))}>Select all</button>
          <button className="ghost" disabled={selectedBackups.length === 0} onClick={() => setSelectedBackupIds([])}>Clear</button>
          <button className="danger" disabled={selectedBackups.length === 0} onClick={() => setDeleteTargets(selectedBackups)}><Trash2 size={16} /> Delete selected</button>
        </div>
        <DataTable headers={["Select", "Status", "Completion Time", "Type", "Initiated by", "Created", "Policy", "Tables", "Size", "Pinned", "Actions"]} isLoading={backups.isLoading}>
          {backupRows.map((backup) => (
            <tr key={backup.id}>
              <td><input className="row-checkbox" aria-label={`Select backup ${backup.id}`} type="checkbox" checked={selectedBackupIds.includes(backup.id)} disabled={isBackupDeleted(backup)} onChange={(event) => setSelectedBackupIds((ids) => event.target.checked ? [...new Set([...ids, backup.id])] : ids.filter((id) => id !== backup.id))} /></td>
              <td><Status value={backup.status} /></td>
              <td>{formatCompletionTime(backup.endedAt ?? backup.deletedAt, backup.startedAt, backup.createdAt)}</td>
              <td>{backup.backupType}</td>
              <td>{backupInitiator(backup, scheduleById)}</td>
              <td>{formatTime(backup.createdAt)}</td>
              <td>{backupPolicyLink(backup.policyId, policyById)}</td>
              <td>{backup.tableCount}</td>
              <td>{formatBytes(backup.backupSizeBytes)}</td>
              <td>{backup.isPinned ? "yes" : "no"}</td>
              <td className="actions">
                <Link className="ghost" to={`/backups/${backup.id}`}>Details</Link>
                {!isBackupDeleted(backup) && <button className="ghost" onClick={() => mutation.mutate({ id: backup.id, action: backup.isPinned ? "unpin" : "pin" })}>{backup.isPinned ? "Unpin" : "Pin"}</button>}
                {isBackupInExecutionPhase(backup.status) && <button className="danger" onClick={() => mutation.mutate({ id: backup.id, action: "cancel" })}><Ban size={16} /> Cancel</button>}
                {!isBackupDeleted(backup) && <button className="danger" onClick={() => setDeleteTargets([backup])}>Delete</button>}
              </td>
            </tr>
          ))}
        </DataTable>
      </section>
      {deleteTargets && <ConfirmDialog title={deleteTargets.length === 1 ? "Delete backup" : "Delete backups"} message={deleteBackupsMessage(deleteTargets)} confirmLabel={deleteTargets.some((backup) => backup.isPinned) ? (deleteTargets.length === 1 ? "Force delete backup" : "Force delete backups") : (deleteTargets.length === 1 ? "Delete backup" : "Delete backups")} busy={deleteMutation.isPending} onCancel={() => setDeleteTargets(null)} onConfirm={() => deleteMutation.mutate(deleteTargets)} />}
      {selectedBackupId && <BackupDrawer backupId={selectedBackupId} onClose={() => { setSelectedBackupId(null); navigate("/backups"); }} onOpenBackup={(id) => { setSelectedBackupId(id); navigate(`/backups/${id}`); }} />}
    </Page>
  );
}

export function BackupDrawer({ backupId, onClose, onOpenBackup }: { backupId: string; onClose: () => void; onOpenBackup?: (backupId: string) => void }) {
  const { api, showToast } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const backup = useQuery({
    queryKey: ["backup", backupId, "summary"],
    queryFn: () => api.backup(backupId, { includeTables: false }),
    refetchInterval: (query) => isBackupInExecutionPhase(query.state.data?.status) ? 3000 : false
  });
  const schedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const scheduleById = new Map((schedules.data ?? []).map((schedule) => [schedule.id, schedule]));
  const current = backup.data;
  const tableDetail = useQuery({
    queryKey: ["backup", backupId, "tables"],
    queryFn: () => api.backup(backupId, { includeTables: true }),
    enabled: !!current && current.contentMode !== "SchemaOnly",
    refetchInterval: isBackupInExecutionPhase(current?.status) ? 3000 : false
  });
  const tableRows = tableDetail.data?.tables ?? [];
  const shardCompletion = current?.contentMode === "SchemaOnly" ? null : calculateBackupShardCompletion(tableRows);
  const detailBackupSizeBytes = calculateBackupSizeBytes(tableRows) ?? current?.backupSizeBytes ?? null;
  const isActive = isBackupInExecutionPhase(current?.status);
  const relatedLogs = useQuery({
    queryKey: ["backup-related-logs", backupId, current?.createdAt],
    queryFn: () => api.logs({ operationId: backupId, last: 500 }),
    enabled: !!current,
    refetchInterval: isActive ? 3000 : false
  });
  const relatedAudits = useQuery({
    queryKey: ["backup-related-audit", backupId, current?.createdAt],
    queryFn: () => api.audits({ operationId: backupId, last: 500 }),
    enabled: !!current,
    refetchInterval: isActive ? 3000 : false
  });
  const backupLogs = relatedLogs.data?.items ?? [];
  const backupAudits = relatedAudits.data?.items ?? [];
  const cancelBackup = useMutation({
    mutationFn: () => api.cancelBackup(backupId),
    onSuccess: () => { backup.refetch(); queryClient.invalidateQueries({ queryKey: ["backups"] }); showToast({ kind: "success", text: "Backup canceled." }); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const bumpBackup = useMutation({
    mutationFn: () => api.moveQueueOperation("Backup", backupId, { direction: "Top", beforeItemId: null as unknown as string }),
    onSuccess: () => { showToast({ kind: "success", text: "Backup queue items moved to top." }); queryClient.invalidateQueries({ queryKey: ["queue"] }); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const canShowFooterActions = isActive || (current ? isBackupStatusRestorable(current.status) : false);
  useEffect(() => {
    if (!current) return;
    queryClient.setQueryData<BackupDto[]>(["backups"], (items) => items?.map((item) => item.id === current.id ? current : item));
  }, [current, queryClient]);
  return (
    <Drawer title="Backup detail" className="backup-detail-drawer" onClose={onClose}>
      <div className="section-head">
        <div>
          <h3>Run status</h3>
          {isActive && <span className="hint">Auto-refreshing while this backup is active.</span>}
        </div>
        {isActive && <button className="secondary" disabled={bumpBackup.isPending} onClick={() => bumpBackup.mutate()}><ArrowUpToLine size={16} /> Bump queue</button>}<button className="secondary" disabled={backup.isFetching || tableDetail.isFetching || relatedLogs.isFetching || relatedAudits.isFetching} onClick={() => { backup.refetch(); tableDetail.refetch(); relatedLogs.refetch(); relatedAudits.refetch(); }}><RefreshCw size={16} /> Refresh</button>
      </div>
      {!current && backup.isLoading && <Empty text="Loading backup details." />}
      {!current && backup.error && <Empty text={String(backup.error)} />}
      {current && <>
        <div className="detail-list">
          <Detail label="Backup id" value={current.id} />
          <Detail label="Status" value={<Status value={current.status} />} />
          <Detail label="Completion Time" value={formatCompletionTime(current.endedAt ?? current.deletedAt, current.startedAt, current.createdAt)} />
          <Detail label="Initiated by" value={backupInitiator(current, scheduleById)} />
          <Detail label="Backup size" value={formatBytes(detailBackupSizeBytes)} />
          <Detail label="Table-shard completion" value={shardCompletion ? <ShardCompletionBadge completion={shardCompletion} /> : "Schema-only"} />
          <Detail label="Related full backups" value={current.backupType === "Full" ? "-" : <RelatedBackupLinks backupIds={current.relatedFullBackupIds} onOpenBackup={onOpenBackup} />} />
          <Detail label="Child backups" value={<RelatedBackupLinks backupIds={current.childBackupIds} onOpenBackup={onOpenBackup} />} />
          <Detail className="detail-wide" label="Failure" value={current.failureReason || current.error ? <ExpandableErrorText text={current.failureReason ?? current.error} title={`Backup ${current.id} failure`} /> : "none"} />
        </div>
        <section className="detail-section detail-section-tables">
          <h3>Tables and shards</h3>
          {current.contentMode === "SchemaOnly" ? <Empty text={`Schema-only backup captured ${current.tableCount} table schema${current.tableCount === 1 ? "" : "s"}; data backup execution was skipped.`} /> : <BackupTablesTable tableRows={tableRows} isLoading={tableDetail.isLoading} />}
        </section>
        <section className="detail-section detail-section-audit">
          <h3>Related audit</h3>
          <DataTable headers={["Time", "Action", "Entity", "Details"]} isLoading={relatedAudits.isLoading}>
            {backupAudits.map((entry) => (
              <tr key={entry.id}>
                <td>{formatTimeSeconds(entry.timestamp)}</td>
                <td>{entry.action}</td>
                <td>{entry.entityType}:{entry.entityId ?? ""}</td>
                <td className="mono wide-cell">{JSON.stringify(entry.details)}</td>
              </tr>
            ))}
          </DataTable>
        </section>
        <section className="detail-section detail-section-logs">
          <h3>Related logs</h3>
          <DataTable headers={["Time", "Level", "Category", "Message", "Exception details"]} isLoading={relatedLogs.isLoading}>
            {backupLogs.map((entry) => (
              <tr key={entry.id}>
                <td>{formatTimeSeconds(entry.timestamp)}</td>
                <td>{entry.level}</td>
                <td>{entry.category}</td>
                <td className="wide-cell">{entry.message}</td>
                <td className="mono wide-cell">{entry.exception ?? ""}</td>
              </tr>
            ))}
          </DataTable>
        </section>
        {canShowFooterActions && <div className="drawer-footer">{isActive && <button className="danger" disabled={cancelBackup.isPending} onClick={() => cancelBackup.mutate()}><Ban size={16} /> Cancel</button>}{isBackupStatusRestorable(current.status) && <button className="primary" onClick={() => navigate("/restores/start", { state: { backupId: current.id } })}><RotateCcw size={16} /> Start restore</button>}</div>}
      </>}
    </Drawer>
  );
}

export function BackupTablesTable({ tableRows, isLoading }: { tableRows: BackupTableDto[]; isLoading: boolean }) {
  const [errorDetail, setErrorDetail] = useState<{ title: string; error: string } | null>(null);
  return <>
  <DataTable headers={["Table", "Engine", "Shard", "Status", "Source node", "Size", "Storage path", "Details"]} isLoading={isLoading}>
    {tableRows.flatMap((table) => {
      if (table.shards.length === 0) {
        return [
          <tr key={table.id}>
            <td>{table.database}.{table.table}</td>
            <td>{table.engine}</td>
            <td>table</td>
            <td><Status value={table.status} /></td>
            <td>none</td>
            <td>{formatBytes(calculateTableSizeBytes(table))}</td>
            <td className="mono wide-cell">{table.storagePath}</td>
            <td>{table.error ? <ErrorDetailButton label={`${table.database}.${table.table}`} error={table.error} onOpen={setErrorDetail} /> : ""}</td>
          </tr>
        ];
      }

      return table.shards
        .slice()
        .sort((left, right) => left.sourceShardNumber - right.sourceShardNumber || left.replicaNumber - right.replicaNumber)
        .map((shard) => (
          <tr key={`${table.id}-${shard.id}`}>
            <td>{table.database}.{table.table}</td>
            <td>{table.engine}</td>
            <td>{formatShardLabel(shard)}</td>
            <td><Status value={shard.status} /></td>
            <td>{formatShardEndpoint(shard)}</td>
            <td>{formatBytes(shard.backupSizeBytes)}</td>
            <td className="mono wide-cell">{shard.storagePath}</td>
            <td>{shard.error ? <ErrorDetailButton label={`${table.database}.${table.table} ${formatShardLabel(shard)}`} error={shard.error} onOpen={setErrorDetail} /> : ""}</td>
          </tr>
        ));
    })}
  </DataTable>
  {errorDetail && <ErrorDetailDialog title={errorDetail.title} error={errorDetail.error} onClose={() => setErrorDetail(null)} />}
  </>;
}

function ErrorDetailButton({ label, error, onOpen }: { label: string; error: string; onOpen: (detail: { title: string; error: string }) => void }) {
  return <button type="button" className="ghost icon-button" title="Show failure details" aria-label={`Show failure details for ${label}`} onClick={() => onOpen({ title: label, error })}><Info size={16} /></button>;
}

export function calculateTableSizeBytes(table: BackupTableDto) {
  if (table.backupSizeBytes !== null && table.backupSizeBytes !== undefined) return table.backupSizeBytes;
  const shardSizes = table.shards.map((shard) => shard.backupSizeBytes).filter((size): size is number => size !== null && size !== undefined);
  return shardSizes.length === 0 ? null : shardSizes.reduce((total, size) => total + size, 0);
}

export function calculateBackupSizeBytes(tables: BackupTableDto[]) {
  const tableSizes = tables.map(calculateTableSizeBytes).filter((size): size is number => size !== null && size !== undefined);
  return tableSizes.length === 0 ? null : tableSizes.reduce((total, size) => total + size, 0);
}

export type BackupShardProgressSummary = {
  shardCount: number;
  queued: number;
  running: number;
  completed: number;
  succeeded: number;
  failed: number;
  skipped: number;
};

export type BackupShardCompletion = {
  succeeded: number;
  failed: number;
  total: number;
  percent: number;
  tone: "ok" | "warn" | "bad";
};

export function calculateBackupShardCompletion(tables: BackupTableDto[]): BackupShardCompletion {
  const statuses = tables.flatMap((table) => table.shards.length > 0 ? table.shards.map((shard) => shard.status) : [table.status]);
  const total = statuses.length;
  const succeeded = statuses.filter((status) => status === "Succeeded").length;
  const failed = statuses.filter((status) => status === "Failed").length;
  const percent = total === 0 ? 0 : Math.round((succeeded / total) * 100);
  return { succeeded, failed, total, percent, tone: failed > 0 ? "bad" : total > 0 && succeeded === total ? "ok" : "warn" };
}

function ShardCompletionBadge({ completion }: { completion: BackupShardCompletion }) {
  return <span className={`backup-completion ${completion.tone}`}>{completion.percent}% ({completion.succeeded}/{completion.total} table-shards)</span>;
}

function RelatedBackupLinks({ backupIds, onOpenBackup }: { backupIds: string[]; onOpenBackup?: (backupId: string) => void }) {
  if (backupIds.length === 0) return <>-</>;
  return <span className="related-backup-links">
    {backupIds.map((id, index) => <span key={id}>{index > 0 && <span>, </span>}{onOpenBackup ? <button className="link-button mono" onClick={() => onOpenBackup(id)}>{id}</button> : <Link className="mono" to={`/backups/${id}`}>{id}</Link>}</span>)}
  </span>;
}
export function summarizeBackupShards(shards: BackupTableShardDto[]): BackupShardProgressSummary {
  const summary: BackupShardProgressSummary = { shardCount: shards.length, queued: 0, running: 0, completed: 0, succeeded: 0, failed: 0, skipped: 0 };
  for (const shard of shards) {
    if (shard.status === "Queued") summary.queued += 1;
    if (shard.status === "Running") summary.running += 1;
    if (shard.status === "Succeeded") summary.succeeded += 1;
    if (shard.status === "Failed") summary.failed += 1;
    if (shard.status === "Skipped") summary.skipped += 1;
  }
  summary.completed = summary.succeeded + summary.failed + summary.skipped;
  return summary;
}

function formatShardLabel(shard: BackupTableShardDto) {
  const shardName = shard.sourceShardName ? ` (${shard.sourceShardName})` : "";
  return `Shard ${shard.sourceShardNumber}${shardName}, replica ${shard.replicaNumber}`;
}

function formatShardEndpoint(shard: BackupTableShardDto) {
  return `${shard.host}:${shard.port}${shard.useTls ? " tls" : ""}`;
}

function deleteBackupsMessage(backups: BackupDto[]) {
  if (backups.length === 1) {
    const backup = backups[0];
    const pinnedText = backup.isPinned ? " This backup is pinned, so confirming will force the delete request." : "";
    const childText = backup.childBackupIds.length === 0 ? " Any associated child backups will also be deleted." : ` ${backup.childBackupIds.length} associated child backup${backup.childBackupIds.length === 1 ? "" : "s"} will also be deleted.`;
    return `Delete backup ${backup.id} and its stored backup data? This is destructive and cannot be undone.${childText}${pinnedText}`;
  }

  const pinnedCount = backups.filter((backup) => backup.isPinned).length;
  const pinnedText = pinnedCount > 0 ? ` ${pinnedCount} selected backup${pinnedCount === 1 ? " is" : "s are"} pinned, so confirming will force those delete requests.` : "";
  const childBackupCount = new Set(backups.flatMap((backup) => backup.childBackupIds)).size;
  const childText = childBackupCount === 0 ? " Any associated child backups will also be deleted." : ` ${childBackupCount} associated child backup${childBackupCount === 1 ? "" : "s"} will also be deleted.`;
  return `Delete ${backups.length} backups and their stored backup data? This is destructive and cannot be undone.${childText}${pinnedText}`;
}

function isBackupDeleted(backup: BackupDto) {
  return Boolean(backup.deletedAt) || backup.status === "ManualDeleted" || backup.status === "FailedBackupDeletedByGarbageCollector" || backup.status === "BackupExpiredDeleted";
}
function backupPolicyLink(policyId: string | null | undefined, policyById: Map<string, { name: string }>) {
  if (!policyId) return "manual";
  return <Link to={`/policies/${policyId}`}>{policyById.get(policyId)?.name ?? policyId}</Link>;
}
function backupInitiator(backup: BackupDto, scheduleById: Map<string, { name: string }>) {
  if (backup.triggerType === "Scheduled" && backup.scheduleId) {
    return <Link to={`/schedules/${backup.scheduleId}`}>{scheduleById.get(backup.scheduleId)?.name ?? backup.scheduleId}</Link>;
  }

  return backup.requestedByName || "manual";
}

function ManualBackupPanel({ policies, onQueued }: { policies: BackupPolicyDto[]; onQueued: () => void }) {
  const { api, showToast } = useApi();
  const [policyId, setPolicyId] = useState(policies[0]?.id ?? "");
  const policy = policies.find((item) => item.id === policyId);
  const [backupType, setBackupType] = useState<BackupType>("Full");
  const settingsPreview = useQuery({ queryKey: ["backup-settings-preview", policyId], queryFn: () => policy ? api.backupSettingsPreview({ clusterId: policy.sourceClusterId, policyId: policy.id }) : Promise.resolve({ settings: {}, sources: [] }), enabled: Boolean(policy) });
  const [clickHouseSettings, setClickHouseSettings] = useState<ClickHouseSettings>({});
  const [settingsValid, setSettingsValid] = useState(true);
  useEffect(() => { setClickHouseSettings((settingsPreview.data?.settings ?? {}) as ClickHouseSettings); }, [settingsPreview.data, policyId]);
  const canIncremental = policy?.contentMode === "SchemaAndData";
  const manual = useMutation({
    mutationFn: () => {
      if (!policy) throw new Error("Choose a policy.");
      return api.manualBackup({
        clusterId: policy.sourceClusterId,
        targetId: policy.contentMode === "SchemaOnly" ? (null as unknown as string) : policy.targetId,
        selector: policy.selector,
        backupType: policy.contentMode === "SchemaOnly" ? "Full" : backupType,
        policyId: policy.id,
        schemaOnly: policy.contentMode === "SchemaOnly",
        clickHouseBackupSettings: clickHouseSettings
      });
    },
    onSuccess: () => { showToast({ kind: "success", text: "Manual backup queued." }); onQueued(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  return <section className="panel form-panel">
    <div className="section-head"><h2>Manual backup</h2><span className="hint">Schema-only policies always run as full schema captures.</span></div>
    <div className="form-grid">
      <Select label="Policy" value={policyId} onChange={(value) => { setPolicyId(value); const selected = policies.find((item) => item.id === value); if (selected?.contentMode === "SchemaOnly") setBackupType("Full"); }} options={policies.map((item) => [item.id, `${item.name} (${item.contentMode === "SchemaOnly" ? "schema only" : "schema + data"})`])} />
      <Select label="Backup type" value={policy?.contentMode === "SchemaOnly" ? "Full" : backupType} onChange={(value) => setBackupType(value as BackupType)} options={canIncremental ? [["Full", "Full"], ["Incremental", "Incremental"]] : [["Full", "Full"]]} />
    </div>
    <ClickHouseAdvancedSettingsEditor title="ClickHouse backup settings for this run" value={clickHouseSettings} sources={(settingsPreview.data?.sources ?? []) as any} onChange={setClickHouseSettings} onValidityChange={setSettingsValid} />
    {settingsPreview.isError && <span className="field-error">{String(settingsPreview.error)}</span>}
    <div className="actions"><button className="primary" disabled={!policy || !settingsValid || settingsPreview.isLoading || settingsPreview.isError || manual.isPending} onClick={() => manual.mutate()}><Play size={16} /> Queue backup</button></div>
  </section>;
}
function isBackupInExecutionPhase(status: BackupRunStatus | undefined) {
  return status === "Queued" || status === "Running";
}


function formatTimeSeconds(value?: string | null) {
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
function defaultBackupFromFilter() {
  const value = new Date();
  value.setDate(value.getDate() - 14);
  return toDateTimeLocal(value);
}

function toDateTimeLocal(value: Date) {
  const pad = (input: number) => input.toString().padStart(2, "0");
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}T${pad(value.getHours())}:${pad(value.getMinutes())}`;
}

function toApiDateTime(value: string) {
  return value ? new Date(value).toISOString() : undefined;
}
