import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, ChevronsUp, ExternalLink, Play, RefreshCw, Table2 } from "lucide-react";
import { Link } from "react-router-dom";
import { BackupDrawer } from "./BackupsPage";
import type { BackupRestoreQueueItemDto, BackupRestoreQueueKind, BackupRestoreQueueMoveDirection } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Page, Select, Status } from "../components/ui";
import { formatTime } from "../utils/format";

export function QueuePage() {
  const { api, showToast } = useApi();
  const [kind, setKind] = useState<BackupRestoreQueueKind>("All");
  const [selectedBackupId, setSelectedBackupId] = useState<string | null>(null);
  const queue = useQuery({
    queryKey: ["backup-restore-queue", kind],
    queryFn: () => api.queue({ kind, status: "active" }),
    refetchInterval: (query) => (query.state.data?.length ?? 0) > 0 ? 3000 : false
  });
  const action = useMutation({
    mutationFn: async ({ item, direction, table }: { item: BackupRestoreQueueItemDto; direction?: BackupRestoreQueueMoveDirection; table?: boolean }) => {
      if (!direction) {
        await api.forceQueueItem(item.id);
        return;
      }
      if (table) {
        await api.moveQueueTable(item.kind, item.tableId, { direction, beforeItemId: null as unknown as string });
        return;
      }
      await api.moveQueueItem(item.id, { direction, beforeItemId: null as unknown as string });
    },
    onSuccess: () => { queue.refetch(); showToast({ kind: "success", text: "Queue updated." }); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const rows = queue.data ?? [];
  return <Page title="Queue" subtitle="Inspect and prioritize active backup and restore shard work." action={<button className="secondary" disabled={queue.isFetching} onClick={() => queue.refetch()}><RefreshCw size={16} /> Refresh</button>}>
    <section className="panel queue-panel">
      <div className="form-grid compact-grid">
        <Select label="Kind" value={kind} onChange={(value) => setKind(value as BackupRestoreQueueKind)} options={[["All", "All"], ["Backup", "Backups"], ["Restore", "Restores"]]} />
      </div>
      <DataTable headers={["Pos", "Kind", "Status", "Run", "Table", "Shard", "Node", "Forced", "Created", "Started", "Actions"]} isLoading={queue.isLoading}>
        {rows.map((item) => {
          const queued = item.status === "Queued";
          return <tr key={item.id}>
            <td>{item.position}</td>
            <td>{item.kind}</td>
            <td><Status value={item.status} /></td>
            <td className="mono queue-run-cell"><QueueRunLink item={item} onOpenBackup={setSelectedBackupId} /></td>
            <td>{item.database}.{item.table}</td>
            <td>{item.logicalShardNumber}{item.logicalShardName ? ` (${item.logicalShardName})` : ""}</td>
            <td>{item.nodeHost ? `${item.nodeHost}:${item.nodePort}` : "pending"}</td>
            <td>{item.isForced ? (item.forcedByName ?? "yes") : "no"}</td>
            <td>{formatTime(item.createdAt)}</td>
            <td>{formatTime(item.startedAt)}</td>
            <td className="queue-actions">
              <button className="ghost icon-button" title="Move row up" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Up" })}><ArrowUp size={16} /></button>
              <button className="ghost icon-button" title="Move row down" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Down" })}><ArrowDown size={16} /></button>
              <button className="ghost icon-button" title="Move row to top" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Top" })}><ChevronsUp size={16} /></button>
              <button className="ghost icon-button" title="Move table to top" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Top", table: true })}><Table2 size={16} /></button>
              <QueueDetailsAction item={item} onOpenBackup={setSelectedBackupId} />
              <button className="primary icon-button" title="Force row" disabled={!queued || action.isPending} onClick={() => action.mutate({ item })}><Play size={16} /></button>
            </td>
          </tr>;
        })}
      </DataTable>
    </section>
    {selectedBackupId && <BackupDrawer backupId={selectedBackupId} onClose={() => setSelectedBackupId(null)} onOpenBackup={setSelectedBackupId} />}
  </Page>;
}

function QueueRunLink({ item, onOpenBackup }: { item: BackupRestoreQueueItemDto; onOpenBackup: (backupId: string) => void }) {
  if (item.kind === "Restore") return <Link to={detailsPath(item)}>{item.operationId}</Link>;
  return <button className="link-button mono" onClick={() => onOpenBackup(item.operationId)}>{item.operationId}</button>;
}

function QueueDetailsAction({ item, onOpenBackup }: { item: BackupRestoreQueueItemDto; onOpenBackup: (backupId: string) => void }) {
  if (item.kind === "Restore") {
    return <Link className="secondary icon-button" title="Open restore details" to={detailsPath(item)}><ExternalLink size={16} /></Link>;
  }

  return <button className="secondary icon-button" title="Open backup details" onClick={() => onOpenBackup(item.operationId)}><ExternalLink size={16} /></button>;
}
function detailsPath(item: BackupRestoreQueueItemDto) {
  return item.kind === "Restore" ? `/restores/${item.operationId}` : `/backups/${item.operationId}`;
}
