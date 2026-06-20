import { useQuery } from "@tanstack/react-query";
import { ExternalLink, RefreshCw } from "lucide-react";
import { useApi } from "../api-context";
import { DataTable, Page } from "../components/ui";

export function MonitoringPage() {
  const { api, showToast } = useApi();
  const metrics = useQuery({ queryKey: ["metrics"], queryFn: () => api.metrics() });
  const entries = Object.entries(metrics.data ?? {}).sort(([left], [right]) => left.localeCompare(right));

  const openJsonMetrics = () => openTextDocument(() => api.metricsJsonText(), "application/json", (error) => showToast({ kind: "error", text: String(error) }));

  return (
    <Page title="Monitoring" subtitle="View server runtime metrics and download the raw metrics payload for diagnostics." action={<button className="secondary" onClick={() => { metrics.refetch(); }}><RefreshCw size={16} /> Refresh</button>}>
      <section className="panel monitoring-links">
        <div>
          <h2>Metrics endpoint</h2>
          <p className="hint">This endpoint uses the same Chobo API authentication as the rest of the server.</p>
        </div>
        <div className="actions">
          <button className="secondary" onClick={openJsonMetrics}><ExternalLink size={16} /> JSON metrics</button>
        </div>
      </section>
      <section className="panel">
        <h2>JSON metrics</h2>
        <DataTable headers={["Metric", "Value"]}>
          {entries.map(([name, value]) => <tr key={name}><td className="mono">{name}</td><td>{value ?? "null"}</td></tr>)}
        </DataTable>
      </section>
    </Page>
  );
}

function openTextDocument(loadText: () => Promise<string>, type: string, onError: (error: unknown) => void) {
  const popup = window.open("about:blank", "_blank");
  if (popup) {
    popup.document.title = "Chobo metrics";
    popup.document.body.textContent = "Loading...";
  }

  loadText()
    .then((text) => {
      const blob = new Blob([text], { type });
      const url = URL.createObjectURL(blob);
      if (popup) popup.location.href = url;
      else window.open(url, "_blank", "noopener,noreferrer");
      window.setTimeout(() => URL.revokeObjectURL(url), 60000);
    })
    .catch((error) => {
      if (popup) popup.document.body.textContent = `Failed to load metrics: ${String(error)}`;
      onError(error);
    });
}
