import { useQuery } from "@tanstack/react-query";
import { ExternalLink, RefreshCw } from "lucide-react";
import { useApi } from "../api-context";
import { DataTable, Empty, Page } from "../components/ui";

export function MonitoringPage() {
  const { api } = useApi();
  const metrics = useQuery({ queryKey: ["metrics"], queryFn: () => api.metrics() });
  const prometheus = useQuery({ queryKey: ["metrics", "prometheus"], queryFn: () => api.prometheusMetrics() });
  const entries = Object.entries(metrics.data ?? {}).sort(([left], [right]) => left.localeCompare(right));

  return (
    <Page title="Monitoring" action={<button className="secondary" onClick={() => { metrics.refetch(); prometheus.refetch(); }}><RefreshCw size={16} /> Refresh</button>}>
      <section className="panel monitoring-links">
        <div>
          <h2>Metrics endpoints</h2>
          <p className="hint">These endpoints use the same Chobo API authentication as the rest of the server.</p>
        </div>
        <div className="actions">
          <a className="secondary" href="/api/v1/metrics" target="_blank" rel="noreferrer"><ExternalLink size={16} /> JSON metrics</a>
          <a className="secondary" href="/api/v1/metrics/prometheus" target="_blank" rel="noreferrer"><ExternalLink size={16} /> Prometheus exporter</a>
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
