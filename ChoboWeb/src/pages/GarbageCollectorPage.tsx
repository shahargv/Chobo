import { useMutation, useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { Play, RefreshCw } from "lucide-react";
import { useApi } from "../api-context";
import { DataTable, Page, Stat, Status } from "../components/ui";
import { formatTime } from "../utils/format";

const cleanupActions = ["garbage", "cleanup", "delete", "retention"];

function formatEntity(entityType: string, entityId?: string | null) {
  if (entityType === "backup" && entityId) {
    return <Link to={`/backups/${entityId}`}>{entityType}:{entityId}</Link>;
  }

  return `${entityType}:${entityId ?? ""}`;
}
export function GarbageCollectorPage() {
  const { api, showToast } = useApi();
  const status = useQuery({ queryKey: ["backup-garbage-collector-status"], queryFn: () => api.backupGarbageCollectorStatus(), refetchInterval: (query) => query.state.data?.isRunning ? 2000 : false });
  const audits = useQuery({ queryKey: ["backup-garbage-collector-audit"], queryFn: () => api.audits({ last: 500 }) });
  const run = useMutation({
    mutationFn: () => api.runBackupGarbageCollector(),
    onSuccess: () => {
      showToast({ kind: "success", text: "Backup garbage collector started." });
      status.refetch();
      audits.refetch();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const current = status.data;
  const entries = (audits.data?.items ?? []).filter((entry) =>
    entry.entityType === "backup-garbage-collector" ||
    (entry.entityType === "backup" && cleanupActions.some((action) => entry.action.includes(action))));

  return (
    <Page title="Garbage Collector" subtitle="Review cleanup activity and run cleanup for expired or failed backup remains." action={<div className="actions"><button className="secondary" disabled={status.isFetching || audits.isFetching} onClick={() => { status.refetch(); audits.refetch(); }}><RefreshCw size={16} /> Refresh</button><button className="primary" disabled={run.isPending || current?.isRunning} onClick={() => run.mutate()}><Play size={16} /> Manual Execute</button></div>}>
      <section className="panel">
        <div className="section-head">
          <div>
            <h2>Current status</h2>
            <p>{current?.isRunning ? `Running ${current.currentRunReason}.` : "Between runs."}</p>
          </div>
          <Status value={current?.isRunning ? "Running" : "Between runs"} />
        </div>
        <div className="stat-grid">
          <Stat label="Marked" value={current?.lastMarkedCount ?? 0} />
          <Stat label="Pending" value={current?.lastPendingCleanupCount ?? 0} />
          <Stat label="Cleaned" value={current?.lastCleanedCount ?? 0} tone={(current?.lastCleanedCount ?? 0) > 0 ? "ok" : undefined} />
          <Stat label="Failed" value={current?.lastFailedCount ?? 0} tone={(current?.lastFailedCount ?? 0) > 0 ? "bad" : undefined} />
        </div>
        <div className="detail-list">
          <div><span>Last started</span><strong>{formatTime(current?.lastStartedAt)}</strong></div>
          <div><span>Last completed</span><strong>{formatTime(current?.lastCompletedAt)}</strong></div>
          <div><span>Last error</span><strong>{current?.lastError ?? "none"}</strong></div>
        </div>
      </section>
      <section className="panel">
        <div className="section-head"><h2>Cleanup audit</h2><span className="hint">{entries.length} cleanup-related audit records shown.</span></div>
        <DataTable headers={["Time", "Actor", "Action", "Entity", "Details"]} isLoading={audits.isLoading}>
          {entries.map((entry) => (
            <tr key={entry.id}>
              <td>{formatTime(entry.timestamp)}</td>
              <td>{entry.actorName}</td>
              <td>{entry.action}</td>
              <td>{formatEntity(entry.entityType, entry.entityId)}</td>
              <td className="mono wide-cell">{JSON.stringify(entry.details)}</td>
            </tr>
          ))}
        </DataTable>
      </section>
    </Page>
  );
}


