import { useState } from "react";
import { Download, Upload } from "lucide-react";
import { useApi } from "../api-context";
import { Page } from "../components/ui";
export function ImportExport() {
  const { api, showToast } = useApi();
  const [text, setText] = useState("");
  const download = async (kind: "data" | "config") => {
    const envelope = kind === "data" ? await api.exportData() : await api.exportConfig();
    const blob = new Blob([JSON.stringify(envelope, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `chobo-${kind}.json`;
    link.click();
    URL.revokeObjectURL(url);
  };
  const upload = async (kind: "data" | "config") => {
    const envelope = JSON.parse(text);
    if (kind === "data") await api.importData(envelope);
    else await api.importConfig(envelope);
    showToast({ kind: "success", text: `${kind} imported.` });
  };
  return (
    <Page title="Import/Export">
      <section className="panel actions-panel">
        <button className="secondary" onClick={() => download("config")}><Download size={16} /> Export config</button>
        <button className="secondary" onClick={() => download("data")}><Download size={16} /> Export data</button>
      </section>
      <section className="panel">
        <h2>Import JSON</h2>
        <textarea value={text} onChange={(event) => setText(event.target.value)} placeholder="Paste exported Chobo JSON here" />
        <div className="actions"><button className="primary" onClick={() => upload("config")}><Upload size={16} /> Import config</button><button className="danger" onClick={() => upload("data")}><Upload size={16} /> Import data</button></div>
      </section>
    </Page>
  );
}

