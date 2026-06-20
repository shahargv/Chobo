import { useQuery } from "@tanstack/react-query";
import { ExternalLink, RefreshCw } from "lucide-react";
import { useApi } from "../api-context";
import { DataTable, Empty, Page } from "../components/ui";

export function MonitoringPage() {
  const { api } = useApi();
  const metrics = useQuery({ queryKey: ["metrics"], queryFn: () => api.metrics() });
  const prometheus = useQuery({ queryKey: ["metrics", "prometheus"], queryFn: () => api.prometheusMetrics() });
  const entries = Object.entries(metrics.data ?? {}).sort(([left], [right]) => left.localeCompare(right));

  const openJsonMetrics = () => openTextDocument(() => api.metricsJsonText(), "application/json");
  const openPrometheusMetrics = () => openTextDocument(() => api.prometheusMetrics(), "text/plain");

  return (
    <Page title="Monitoring" action={<button className="secondary" onClick={() => { metrics.refetch(); prometheus.refetch(); }}><RefreshCw size={16} /> Refresh</button>}>
      <section className="panel monitoring-links">
        <div>
          <h2>Metrics endpoints</h2>
          <p className="hint">These endpoints use the same Chobo API authentication as the rest of the server.</p>
        </div>
        <div className="actions">
          <button className="secondary" onClick={openJsonMetrics}><ExternalLink size={16} /> JSON metrics</button>
          <button className="secondary" onClick={openPrometheusMetrics}><ExternalLink size={16} /> Prometheus exporter</button>
        </div>
      </section>
      <section className="panel">
        <h2>JSON metrics</h2>
        <DataTable headers={["Metric", "Value"]}>
          {entries.map(([name, value]) => <tr key={name}><td className="mono">{name}</td><td>{value ?? "null"}</td></tr>)}
        </DataTable>
      </section>
      <section className="panel">
        <h2>Prometheus exporter</h2>
        {prometheus.data?.trim()
          ? <pre>{prometheus.data}</pre>
          : <Empty text={prometheus.isLoading ? "Loading Prometheus metrics." : "No Prometheus metrics are currently exposed."} />}
      </section>
    </Page>
  );
}

function openTextDocument(loadText: () => Promise<string>, type: string) {
  const popup = window.open("about:blank", "_blank");
  if (popup) {
    popup.document.title = "Chobo metrics";
    popup.document.body.textContent = "Loading...";
  }

  loadText().then((text) => {
    const blob = new Blob([text], { type });
    const url = URL.createObjectURL(blob);
    if (popup) popup.location.href = url;
    else window.open(url, "_blank", "noopener,noreferrer");
    window.setTimeout(() => URL.revokeObjectURL(url), 60000);
  });
}


