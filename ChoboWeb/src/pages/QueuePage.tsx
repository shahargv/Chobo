import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, ChevronsUp, Play, RefreshCw } from "lucide-react";
import type { BackupRestoreQueueItemDto, BackupRestoreQueueKind, BackupRestoreQueueMoveDirection } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Page, Select, Status } from "../components/ui";
import { formatTime } from "../utils/format";

export function QueuePage() {
  const { api, showToast } = useApi();
  const [kind, setKind] = useState<BackupRestoreQueueKind>("All");
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
        await api.moveQueueTable(item.kind, item.tableId, { direction });
        return;
      }
      await api.moveQueueItem(item.id, { direction });
    },
    onSuccess: () => { queue.refetch(); showToast({ kind: "success", text: "Queue updated." }); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const rows = queue.data ?? [];
  return <Page title="Queue" subtitle="Inspect and prioritize active backup and restore shard work." action={<button className="secondary" disabled={queue.isFetching} onClick={() => queue.refetch()}><RefreshCw size={16} /> Refresh</button>}>
    <section className="panel">
      <div className="form-grid compact-grid">
        <Select label="Kind" value={kind} onChange={(value) => setKind(value as BackupRestoreQueueKind)} options={[["All", "All"], ["Backup", "Backups"], ["Restore", "Restores"]]} />
      </div>
      <DataTable headers={["Pos", "Kind", "Status", "Run", "Table", "Shard", "Node", "Forced", "Created", "Started", "Error", "Actions"]} isLoading={queue.isLoading}>
        {rows.map((item) => {
          const queued = item.status === "Queued";
          return <tr key={item.id}>
            <td>{item.position}</td>
            <td>{item.kind}</td>
            <td><Status value={item.status} /></td>
            <td className="mono wide-cell">{item.operationId}</td>
            <td>{item.database}.{item.table}</td>
            <td>{item.logicalShardNumber}{item.logicalShardName ? ` (${item.logicalShardName})` : ""}</td>
            <td>{item.nodeHost ? `${item.nodeHost}:${item.nodePort}` : "pending"}</td>
            <td>{item.isForced ? (item.forcedByName ?? "yes") : "no"}</td>
            <td>{formatTime(item.createdAt)}</td>
            <td>{formatTime(item.startedAt)}</td>
            <td className="wide-cell">{item.blockingReason ?? item.error ?? ""}</td>
            <td className="actions">
              <button className="ghost icon-button" title="Move row up" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Up" })}><ArrowUp size={16} /></button>
              <button className="ghost icon-button" title="Move row down" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Down" })}><ArrowDown size={16} /></button>
              <button className="ghost icon-button" title="Move row to top" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Top" })}><ChevronsUp size={16} /></button>
              <button className="ghost" disabled={!queued || action.isPending} onClick={() => action.mutate({ item, direction: "Top", table: true })}>Table top</button>
              <button className="primary icon-button" title="Force row" disabled={!queued || action.isPending} onClick={() => action.mutate({ item })}><Play size={16} /></button>
            </td>
          </tr>;
        })}
      </DataTable>
    </section>
  </Page>;
}