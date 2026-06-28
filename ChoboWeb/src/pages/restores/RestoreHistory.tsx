import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { RotateCcw } from "lucide-react";
import { BackupDrawer } from "../BackupsPage";
import { useApi } from "../../api-context";
import { DataTable, Page, Status } from "../../components/ui";
import { formatCompletionTime, formatTime } from "../../utils/format";
import { formatRestoreLayout } from "./restoreUtils";

export function RestoreHistory() {
  const { api } = useApi();
  const [selectedBackupId, setSelectedBackupId] = useState<string | null>(null);
  const restores = useQuery({ queryKey: ["restores"], queryFn: () => api.restores() });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const clusterById = useMemo(() => new Map((clusters.data ?? []).map((cluster) => [cluster.id, cluster])), [clusters.data]);

  return (
    <Page title="Restores" subtitle="Browse restore history and open detailed status for each restore run." action={<Link className="primary" to="/restores/start"><RotateCcw size={16} /> Start restore</Link>}>
      <section className="panel restore-history-panel">
        <div className="section-head">
          <div>
            <h2>Restore history</h2>
            <p>Queued and completed restores stay here for status, affected tables, failures, logs, and audit follow-up.</p>
          </div>
        </div>
        <DataTable headers={["Status", "Completion Time", "Requested by", "Created", "Backup", "Target", "Layout", "Tables", "Failure", "Actions"]} isLoading={restores.isLoading}>
          {(restores.data ?? []).map((restore) => (
            <tr key={restore.id}>
              <td><Status value={restore.status} /></td>
              <td>{formatCompletionTime(restore.endedAt, restore.startedAt, restore.createdAt)}</td>
              <td>{restore.requestedByName}</td>
              <td>{formatTime(restore.createdAt)}</td>
              <td><button type="button" className="link-button mono" onClick={() => setSelectedBackupId(restore.backupId)}>{restore.backupId}</button></td>
              <td><Link to={`/clusters/${restore.targetClusterId}`}>{clusterById.get(restore.targetClusterId)?.name ?? restore.targetClusterId}</Link></td>
              <td>{formatRestoreLayout(restore.layout)}</td>
              <td>{restore.tables.length}</td>
              <td>{restore.failureReason ?? ""}</td>
              <td className="actions"><Link className="ghost" to={`/restores/${restore.id}`}>Details</Link></td>
            </tr>
          ))}
        </DataTable>
      </section>
      {selectedBackupId && <BackupDrawer backupId={selectedBackupId} onClose={() => setSelectedBackupId(null)} onOpenBackup={setSelectedBackupId} />}
    </Page>
  );
}

