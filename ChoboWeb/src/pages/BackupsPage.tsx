import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { Play, RefreshCw, RotateCcw } from "lucide-react";
import type { BackupDto, BackupRunStatus } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Detail, Drawer, Empty, Page, Status } from "../components/ui";
import { formatTime } from "../utils/format";

export function Backups() {
  const { api, showToast } = useApi();
  const [selectedBackupId, setSelectedBackupId] = useState<string | null>(null);
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const mutation = useMutation({
    mutationFn: ({ id, action }: { id: string; action: "pin" | "unpin" | "delete" }) =>
      action === "pin" ? api.pinBackup(id) : action === "unpin" ? api.unpinBackup(id) : api.deleteBackup(id),
    onSuccess: () => { backups.refetch(); showToast({ kind: "success", text: "Backup updated." }); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Backups" action={<ManualBackupButton />}>
      <section className="panel">
        <DataTable headers={["Status", "Type", "Created", "Policy", "Tables", "Pinned", "Actions"]}>
          {(backups.data ?? []).map((backup) => (
            <tr key={backup.id}>
              <td><Status value={backup.status} /></td>
              <td>{backup.backupType}</td>
              <td>{formatTime(backup.createdAt)}</td>
              <td>{backup.policyId ?? "manual"}</td>
              <td>{backup.tables.length}</td>
              <td>{backup.isPinned ? "yes" : "no"}</td>
              <td className="actions">
                <button className="ghost" onClick={() => setSelectedBackupId(backup.id)}>Details</button>
                <button className="ghost" onClick={() => mutation.mutate({ id: backup.id, action: backup.isPinned ? "unpin" : "pin" })}>{backup.isPinned ? "Unpin" : "Pin"}</button>
                <button className="danger" onClick={() => mutation.mutate({ id: backup.id, action: "delete" })}>Delete</button>
              </td>
            </tr>
          ))}
        </DataTable>
      </section>
      {selectedBackupId && <BackupDrawer backupId={selectedBackupId} onClose={() => setSelectedBackupId(null)} />}
    </Page>
  );
}

function BackupDrawer({ backupId, onClose }: { backupId: string; onClose: () => void }) {
  const { api } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const backup = useQuery({
    queryKey: ["backup", backupId],
    queryFn: () => api.backup(backupId),
    refetchInterval: (query) => isBackupInExecutionPhase(query.state.data?.status) ? 3000 : false
  });
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
        <div className="drawer-footer"><button className="primary" onClick={() => navigate("/restores", { state: { backupId: current.id } })}><RotateCcw size={16} /> Start restore</button></div>
      </>}
    </Drawer>
  );
}

function ManualBackupButton() {
  const navigate = useNavigate();
  return <button className="primary" onClick={() => navigate("/policies")}><Play size={16} /> Manual backup</button>;
}

function isBackupInExecutionPhase(status: BackupRunStatus | undefined) {
  return status === "Queued" ||
    status === "Running" ||
    status === "ManualDeleteRequested" ||
    status === "FailedBackupDeleteRequested" ||
    status === "BackupExpiredDeleteStarted";
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


