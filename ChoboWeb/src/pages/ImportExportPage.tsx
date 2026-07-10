import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, RefreshCw, Upload } from "lucide-react";
import { useApi } from "../api-context";
import { Input, Page, Select } from "../components/ui";
import type { BackupMetadataRecoveryResult, ImportResultDto } from "../api/generated";

export function ImportExport() {
  const { api, showToast } = useApi();
  const queryClient = useQueryClient();
  const [importFile, setImportFile] = useState<File | null>(null);
  const [importFileInputKey, setImportFileInputKey] = useState(0);
  const [recoveryOpen, setRecoveryOpen] = useState(false);
  const [recoveryMode, setRecoveryMode] = useState<"scan" | "path">("scan");
  const [recoveryPath, setRecoveryPath] = useState("");
  const [recoveryTargetId, setRecoveryTargetId] = useState("");
  const [lastImportResult, setLastImportResult] = useState<ImportResultDto | null>(null);
  const [lastRecoveryResult, setLastRecoveryResult] = useState<BackupMetadataRecoveryResult | null>(null);
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
      return kind;
    },
    onSuccess: (kind) => showToast({ kind: "success", text: `${kind} export downloaded.` }),
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  const upload = useMutation({
    mutationFn: async (kind: "data" | "config") => {
      if (!importFile) throw new Error("Choose a Chobo JSON export file first.");
      const envelope = JSON.parse(await importFile.text());
      const result = kind === "data" ? await api.importData(envelope) : await api.importConfig(envelope);
      return { kind, result };
    },
    onSuccess: ({ kind, result }) => {
      queryClient.clear();
      setLastImportResult(result);
      showToast({ kind: result.warning ? "error" : "success", text: result.warning ?? `${kind} imported. Re-enter imported credentials before running backups.` });
      setImportFile(null);
      setImportFileInputKey((key) => key + 1);
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  const recover = useMutation({
    mutationFn: async () => recoveryMode === "scan"
      ? api.recoverBackupFromScan({ targetId: recoveryTargetId, scanRoot: recoveryPath || null })
      : api.recoverBackupFromPath({ targetId: recoveryTargetId, backupPath: recoveryPath || null }),
    onSuccess: (result) => {
      ["backups", "restores", "audit", "logs", "dashboard", "metrics", "backup-garbage-collector-status"].forEach((key) => queryClient.invalidateQueries({ queryKey: [key] }));
      showToast({ kind: result.warning ? "error" : "success", text: result.warning ?? `Recovery finished: ${result.importedBackupCount} imported, ${result.updatedBackupCount} updated, ${result.skippedManifestCount} skipped.` });
      setLastRecoveryResult(result);
      setRecoveryOpen(false);
    }
  });

  const exportingKind = download.isPending ? download.variables : null;

  return (
    <Page title="Import/Export" subtitle="Export restorable metadata or import a previous Chobo configuration or data snapshot.">
      <section className="panel actions-panel">
        <button className="secondary" disabled={download.isPending} onClick={() => download.mutate("config")}><Download size={16} /> {exportingKind === "config" ? "Exporting config..." : "Export config"}</button>
        <button className="secondary" disabled={download.isPending} onClick={() => download.mutate("data")}><Download size={16} /> {exportingKind === "data" ? "Exporting data..." : "Export data"}</button>
        <button className="secondary" disabled={download.isPending} onClick={() => setRecoveryOpen(true)}><RefreshCw size={16} /> Recover backup states</button>
        {exportingKind && <span className="hint">Preparing {exportingKind} export...</span>}
      </section>
      {lastImportResult?.warning && <section className="panel warning-panel" role="alert"><h2>Import completed with unavailable AES keys</h2><p>{lastImportResult.warning}</p><p className="mono">{lastImportResult.missingOrInvalidAesKeyIds.join(", ")}</p><p>{lastImportResult.affectedBackupShardCount} protected backup shard(s) and {lastImportResult.affectedPolicyCount} policy password(s) are affected. Restore the matching files under <code>secrets/aes-keys</code>, then refresh.</p><p><a href="https://github.com/shahargv/Chobo/blob/main/docs/user/Upgrading.md" target="_blank" rel="noreferrer">Open AES-key recovery and upgrade guidance</a></p></section>}
      {lastRecoveryResult?.warning && <section className="panel warning-panel" role="alert"><h2>Recovery completed with unavailable AES keys</h2><p>{lastRecoveryResult.warning}</p><p className="mono">{(lastRecoveryResult.missingOrInvalidAesKeyIds ?? []).join(", ")}</p><p>Restore the matching files under <code>secrets/aes-keys</code>, then refresh. <a href="https://github.com/shahargv/Chobo/blob/main/docs/user/Upgrading.md" target="_blank" rel="noreferrer">Open AES-key recovery and upgrade guidance</a></p></section>}
      <section className="panel">
        <h2>Import file</h2>
        <label className="import-file-field">
          Chobo JSON export
          <input
            key={importFileInputKey}
            type="file"
            accept="application/json,.json"
            onChange={(event) => setImportFile(event.target.files?.[0] ?? null)}
          />
          <span className="hint">{importFile ? importFile.name : "Choose a .json file exported from Chobo."}</span>
        </label>
        <div className="actions"><button className="primary" disabled={upload.isPending || !importFile} onClick={() => upload.mutate("config")}><Upload size={16} /> Import config</button><button className="danger" disabled={upload.isPending || !importFile} onClick={() => upload.mutate("data")}><Upload size={16} /> Import data</button></div>
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

