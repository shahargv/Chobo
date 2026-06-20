import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, RefreshCw, Upload } from "lucide-react";
import { useApi } from "../api-context";
import { Input, Page, Select } from "../components/ui";

export function ImportExport() {
  const { api, showToast } = useApi();
  const queryClient = useQueryClient();
  const [text, setText] = useState("");
  const [recoveryOpen, setRecoveryOpen] = useState(false);
  const [recoveryMode, setRecoveryMode] = useState<"scan" | "path">("scan");
  const [recoveryPath, setRecoveryPath] = useState("");
  const [recoveryTargetId, setRecoveryTargetId] = useState("");
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });

  const download = useMutation({
    mutationFn: async (kind: "data" | "config") => {
      const envelope = kind === "data" ? await api.exportData() : await api.exportConfig();
      const blob = new Blob([JSON.stringify(envelope, null, 2)], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = `chobo-${kind}.json`;
      link.click();
      URL.revokeObjectURL(url);
    }
  });

  const upload = useMutation({
    mutationFn: async (kind: "data" | "config") => {
      const envelope = JSON.parse(text);
      if (kind === "data") await api.importData(envelope);
      else await api.importConfig(envelope);
      return kind;
    },
    onSuccess: (kind) => {
      queryClient.clear();
      showToast({ kind: "success", text: `${kind} imported. Re-enter imported credentials before running backups.` });
    }
  });

  const recover = useMutation({
    mutationFn: async () => recoveryMode === "scan"
      ? api.recoverBackupFromScan({ targetId: recoveryTargetId, scanRoot: recoveryPath || null })
      : api.recoverBackupFromPath({ targetId: recoveryTargetId, backupPath: recoveryPath || null }),
    onSuccess: (result) => {
      ["backups", "restores", "audit", "logs", "dashboard", "metrics", "backup-garbage-collector-status"].forEach((key) => queryClient.invalidateQueries({ queryKey: [key] }));
      showToast({ kind: "success", text: `Recovery finished: ${result.importedBackupCount} imported, ${result.updatedBackupCount} updated, ${result.skippedManifestCount} skipped.` });
      setRecoveryOpen(false);
    }
  });

  return (
    <Page title="Import/Export" subtitle="Export restorable metadata or import a previous Chobo configuration or data snapshot.">
      <section className="panel actions-panel">
        <button className="secondary" disabled={download.isPending} onClick={() => download.mutate("config")}><Download size={16} /> Export config</button>
        <button className="secondary" disabled={download.isPending} onClick={() => download.mutate("data")}><Download size={16} /> Export data</button>
        <button className="secondary" onClick={() => setRecoveryOpen(true)}><RefreshCw size={16} /> Recover backup states</button>
      </section>
      <section className="panel">
        <h2>Import JSON</h2>
        <textarea value={text} onChange={(event) => setText(event.target.value)} placeholder="Paste exported Chobo JSON here" />
        <div className="actions"><button className="primary" disabled={upload.isPending} onClick={() => upload.mutate("config")}><Upload size={16} /> Import config</button><button className="danger" disabled={upload.isPending} onClick={() => upload.mutate("data")}><Upload size={16} /> Import data</button></div>
      </section>
      {recoveryOpen && <section className="panel form-panel">
        <div className="section-head"><h2>Recover backup metadata</h2><button className="ghost" onClick={() => setRecoveryOpen(false)}>Cancel</button></div>
        <div className="form-grid">
          <Select label="Recovery mode" value={recoveryMode} onChange={(value) => setRecoveryMode(value === "path" ? "path" : "scan")} options={[["scan", "Scan configured storage targets"], ["path", "Recover from storage path"]]} />
          {recoveryMode === "path" && <Input label="Storage path" value={recoveryPath} onChange={setRecoveryPath} />}
        </div>
        <div className="actions"><button className="primary" disabled={recover.isPending || recoveryTargetId.length === 0 || (recoveryMode === "path" && recoveryPath.trim().length === 0)} onClick={() => recover.mutate()}><RefreshCw size={16} /> Confirm recovery scan</button></div>
      </section>}
    </Page>
  );
}
