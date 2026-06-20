import { useMemo } from "react";
import { Link, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Ban, RefreshCw } from "lucide-react";
import { useApi } from "../../api-context";
import { DataTable, Detail, Empty, Page, Status } from "../../components/ui";
import { formatCompletionTime, formatTime } from "../../utils/format";
import { formatRestoreLayout, formatTableOptions, formatTimeSeconds, isRestoreInExecutionPhase } from "./restoreUtils";

export function RestoreDetailPage() {
  const { api, showToast } = useApi();
  const { restoreId = "" } = useParams();
  const queryClient = useQueryClient();
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const restore = useQuery({
    queryKey: ["restore", restoreId],
    queryFn: () => api.restore(restoreId),
    enabled: !!restoreId,
    refetchInterval: (query) => isRestoreInExecutionPhase(query.state.data?.status) ? 3000 : false
  });
  const relatedLogs = useQuery({
    queryKey: ["restore-related-logs", restoreId],
    queryFn: () => api.logs({ operationId: restoreId, limit: 10000 }),
    enabled: !!restoreId,
    refetchInterval: () => isRestoreInExecutionPhase(restore.data?.status) ? 3000 : false
  });
  const relatedAudits = useQuery({
    queryKey: ["restore-related-audit", restoreId],
    queryFn: () => api.audits({ operationId: restoreId, limit: 10000 }),
    enabled: !!restoreId,
    refetchInterval: () => isRestoreInExecutionPhase(restore.data?.status) ? 3000 : false
  });
  const current = restore.data;
  const clusterById = useMemo(() => new Map((clusters.data ?? []).map((cluster) => [cluster.id, cluster])), [clusters.data]);
  const backupById = useMemo(() => new Map((backups.data ?? []).map((backup) => [backup.id, backup])), [backups.data]);
  const backup = current ? backupById.get(current.backupId) : undefined;
  const active = isRestoreInExecutionPhase(current?.status);
  const logs = relatedLogs.data?.items ?? [];
  const audits = relatedAudits.data?.items ?? [];
  const cancelRestore = useMutation({
    mutationFn: () => api.cancelRestore(restoreId),
    onSuccess: () => { restore.refetch(); queryClient.invalidateQueries({ queryKey: ["restores"] }); showToast({ kind: "success", text: "Restore canceled." }); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  return (
    <Page title="Restore details" subtitle="Inspect restore progress, table and shard results, and related logs or audit entries." action={<div className="actions"><Link className="secondary" to="/restores"><ArrowLeft size={16} /> History</Link>{active && <button className="danger" disabled={cancelRestore.isPending} onClick={() => cancelRestore.mutate()}><Ban size={16} /> Cancel</button>}<button className="secondary" disabled={restore.isFetching || relatedLogs.isFetching || relatedAudits.isFetching} onClick={() => { restore.refetch(); relatedLogs.refetch(); relatedAudits.refetch(); }}><RefreshCw size={16} /> Refresh</button></div>}>
      {!current && restore.isLoading && <section className="panel"><Empty text="Loading restore details." /></section>}
      {!current && restore.error && <section className="panel"><Empty text={String(restore.error)} /></section>}
      {current && <>
        <section className="panel restore-detail-summary">
          <div className="section-head">
            <div>
              <h2>Run status</h2>
              {active && <p>Auto-refreshing while this restore is active.</p>}
            </div>
            <Status value={current.status} />
          </div>
          <div className="detail-list restore-review-details">
            <Detail label="Restore id" value={current.id} />
            <Detail label="Backup" value={<Link to={`/backups/${current.backupId}`}>{backup ? `${backup.backupType} · ${formatTime(backup.createdAt)}` : current.backupId}</Link>} />
            <Detail label="Target cluster" value={<Link to={`/clusters/${current.targetClusterId}`}>{clusterById.get(current.targetClusterId)?.name ?? current.targetClusterId}</Link>} />
            <Detail label="Layout" value={formatRestoreLayout(current.layout)} />
            <Detail label="Requested by" value={current.requestedByName} />
            <Detail label="Completion Time" value={formatCompletionTime(current.endedAt, current.startedAt, current.createdAt)} />
            <Detail label="Failure" value={current.failureReason ?? current.error ?? "none"} />
          </div>
        </section>
        <section className="panel detail-section restore-detail-section restore-detail-tables">
          <h2>Affected tables</h2>
          <DataTable headers={["Status", "Source", "Target", "Mode", "Options", "Started", "Completed", "Failure"]}>
            {current.tables.map((table) => (
              <tr key={table.id}>
                <td><Status value={table.status} /></td>
                <td>{table.sourceDatabase}.{table.sourceTable}</td>
                <td>{table.targetDatabase}.{table.targetTable}</td>
                <td>{table.schemaOnly ? "Schema only" : "Schema + data"}</td>
                <td>{formatTableOptions(table.append, table.allowSchemaMismatch)}</td>
                <td>{formatTime(table.startedAt)}</td>
                <td>{formatTime(table.completedAt)}</td>
                <td>{table.error ?? table.warning ?? ""}</td>
              </tr>
            ))}
          </DataTable>
        </section>
        <section className="panel detail-section restore-detail-section restore-detail-shards">
          <h2>Status by table and shard</h2>
          <DataTable headers={["Status", "Table", "Source shard", "Target shard", "Target node", "Restore table", "ClickHouse", "Failure"]}>
            {current.tables.flatMap((table) => table.shards.map((shard) => (
              <tr key={shard.id}>
                <td><Status value={shard.status} /></td>
                <td>{table.targetDatabase}.{table.targetTable}</td>
                <td>{shard.sourceShardNumber}</td>
                <td>{shard.targetShardNumber ?? "n/a"}{shard.targetShardName ? ` (${shard.targetShardName})` : ""}</td>
                <td>{shard.targetHost}:{shard.targetPort}</td>
                <td>{shard.restoreDatabase}.{shard.restoreTableName}</td>
                <td>{shard.clickHouseStatus ?? shard.clickHouseOperationId ?? ""}</td>
                <td>{shard.error ?? shard.warning ?? ""}</td>
              </tr>
            )))}
          </DataTable>
        </section>
        <section className="panel detail-section restore-detail-section restore-detail-logs">
          <div className="section-head"><h2>Log messages</h2><span className="hint">{logs.length} of {relatedLogs.data?.totalCount ?? logs.length} shown for this restore operation.</span></div>
          <DataTable headers={["Time", "Level", "Category", "Message"]}>
            {logs.map((entry) => (
              <tr key={entry.id}>
                <td>{formatTimeSeconds(entry.timestamp)}</td>
                <td>{entry.level}</td>
                <td>{entry.category}</td>
                <td className="wide-cell">{entry.message}</td>
              </tr>
            ))}
          </DataTable>
        </section>
        <section className="panel detail-section restore-detail-section restore-detail-audit">
          <div className="section-head"><h2>Audit messages</h2><span className="hint">{audits.length} of {relatedAudits.data?.totalCount ?? audits.length} shown for this restore operation.</span></div>
          <DataTable headers={["Time", "Actor", "Action", "Entity", "Details"]}>
            {audits.map((entry) => (
              <tr key={entry.id}>
                <td>{formatTimeSeconds(entry.timestamp)}</td>
                <td>{entry.actorName}</td>
                <td>{entry.action}</td>
                <td>{entry.entityType}:{entry.entityId ?? ""}</td>
                <td className="mono wide-cell">{JSON.stringify(entry.details)}</td>
              </tr>
            ))}
          </DataTable>
        </section>
      </>}
    </Page>
  );
}

function restoreCompletionType(status: string) {
  switch (status) {
    case "Succeeded": return "Succeeded";
    case "PartiallySucceeded": return "Partial";
    case "Failed": return "Failed";
    case "Canceled": return "Canceled";
    case "Queued": return "Pending";
    case "Running": return "Running";
    default: return status;
  }
}


