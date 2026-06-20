import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Ban, Play, RefreshCw, RotateCcw } from "lucide-react";
import type { BackupDto, BackupPolicyDto, BackupRunStatus, BackupType } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Detail, Drawer, Empty, Page, Select, Status } from "../components/ui";
import { formatCompletionTime, formatTime } from "../utils/format";
import { isBackupStatusRestorable } from "./restores/restoreUtils";

export function Backups() {
  const { api, showToast } = useApi();
  const { backupId } = useParams();
  const navigate = useNavigate();
  const [selectedBackupId, setSelectedBackupId] = useState<string | null>(backupId ?? null);
  const [showManual, setShowManual] = useState(false);
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const schedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const scheduleById = new Map((schedules.data ?? []).map((schedule) => [schedule.id, schedule]));
  const policyById = new Map((policies.data ?? []).map((policy) => [policy.id, policy]));
  useEffect(() => {
    setSelectedBackupId(backupId ?? null);
  }, [backupId]);
  const mutation = useMutation({
    mutationFn: ({ id, action }: { id: string; action: "pin" | "unpin" | "delete" | "cancel" }) =>
      action === "pin" ? api.pinBackup(id) : action === "unpin" ? api.unpinBackup(id) : action === "cancel" ? api.cancelBackup(id) : api.deleteBackup(id),
    onSuccess: () => { backups.refetch(); showToast({ kind: "success", text: "Backup updated." }); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Backups" subtitle="Start manual backups, review backup history, and inspect table or shard results." action={<button className="primary" onClick={() => setShowManual(!showManual)}><Play size={16} /> Manual backup</button>}>
      {showManual && <ManualBackupPanel policies={policies.data ?? []} onQueued={() => { setShowManual(false); backups.refetch(); }} />}
      <section className="panel">
        <DataTable headers={["Status", "Completion Time", "Type", "Initiated by", "Created", "Policy", "Tables", "Pinned", "Actions"]}>
          {(backups.data ?? []).map((backup) => (
            <tr key={backup.id}>
              <td><Status value={backup.status} /></td>
              <td>{formatCompletionTime(backup.endedAt ?? backup.deletedAt, backup.startedAt, backup.createdAt)}</td>
              <td>{backup.backupType}</td>
              <td>{backupInitiator(backup, scheduleById)}</td>
              <td>{formatTime(backup.createdAt)}</td>
              <td>{backupPolicyLink(backup.policyId, policyById)}</td>
              <td>{backup.tables.length}</td>
              <td>{backup.isPinned ? "yes" : "no"}</td>
              <td className="actions">
                <Link className="ghost" to={`/backups/${backup.id}`}>Details</Link>
                {!isBackupDeleted(backup) && <button className="ghost" onClick={() => mutation.mutate({ id: backup.id, action: backup.isPinned ? "unpin" : "pin" })}>{backup.isPinned ? "Unpin" : "Pin"}</button>}
                {isBackupInExecutionPhase(backup.status) && <button className="danger" onClick={() => mutation.mutate({ id: backup.id, action: "cancel" })}><Ban size={16} /> Cancel</button>}
                {!isBackupDeleted(backup) && <button className="danger" onClick={() => mutation.mutate({ id: backup.id, action: "delete" })}>Delete</button>}
              </td>
            </tr>
          ))}
        </DataTable>
      </section>
      {selectedBackupId && <BackupDrawer backupId={selectedBackupId} onClose={() => { setSelectedBackupId(null); navigate("/backups"); }} />}
    </Page>
  );
}

function BackupDrawer({ backupId, onClose }: { backupId: string; onClose: () => void }) {
  const { api, showToast } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const backup = useQuery({
    queryKey: ["backup", backupId],
    queryFn: () => api.backup(backupId),
    refetchInterval: (query) => isBackupInExecutionPhase(query.state.data?.status) ? 3000 : false
  });
  const schedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const scheduleById = new Map((schedules.data ?? []).map((schedule) => [schedule.id, schedule]));
  const current = backup.data;
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
        <button className="secondary" disabled={backup.isFetching || relatedLogs.isFetching || relatedAudits.isFetching} onClick={() => { backup.refetch(); relatedLogs.refetch(); relatedAudits.refetch(); }}><RefreshCw size={16} /> Refresh</button>
      </div>
      {!current && backup.isLoading && <Empty text="Loading backup details." />}
      {!current && backup.error && <Empty text={String(backup.error)} />}
      {current && <>
        <div className="detail-list">
          <Detail label="Backup id" value={current.id} />
          <Detail label="Status" value={<Status value={current.status} />} />
          <Detail label="Completion Time" value={formatCompletionTime(current.endedAt ?? current.deletedAt, current.startedAt, current.createdAt)} />
          <Detail label="Initiated by" value={backupInitiator(current, scheduleById)} />
          <Detail label="Failure" value={current.failureReason ?? current.error ?? "none"} />
        </div>
        <section className="detail-section detail-section-tables">
          <h3>Tables and shards</h3>
          <DataTable headers={["Table", "Engine", "Status", "Shards", "S3 path"]}>
            {current.tables.map((table) => (
              <tr key={table.id}>
                <td>{table.database}.{table.table}</td>
                <td>{table.engine}</td>
                <td><Status value={table.status} /></td>
                <td>{table.shards.length}</td>
                <td className="mono wide-cell">{table.s3Path}</td>
              </tr>
            ))}
          </DataTable>
        </section>
        <section className="detail-section detail-section-audit">
          <h3>Related audit</h3>
          <DataTable headers={["Time", "Action", "Entity", "Details"]}>
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
          <DataTable headers={["Time", "Level", "Category", "Message"]}>
            {backupLogs.map((entry) => (
              <tr key={entry.id}>
                <td>{formatTimeSeconds(entry.timestamp)}</td>
                <td>{entry.level}</td>
                <td>{entry.category}</td>
                <td className="wide-cell">{entry.message}</td>
              </tr>
            ))}
          </DataTable>
        </section>
        <div className="drawer-footer">{isActive && <button className="danger" disabled={cancelBackup.isPending} onClick={() => cancelBackup.mutate()}><Ban size={16} /> Cancel</button>}{isBackupStatusRestorable(current.status) && <button className="primary" onClick={() => navigate("/restores/start", { state: { backupId: current.id } })}><RotateCcw size={16} /> Start restore</button>}</div>
      </>}
    </Drawer>
  );
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
        schemaOnly: policy.contentMode === "SchemaOnly"
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
    <div className="actions"><button className="primary" disabled={!policy || manual.isPending} onClick={() => manual.mutate()}><Play size={16} /> Queue backup</button></div>
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




