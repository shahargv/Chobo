import { useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { RefreshCw, RotateCcw, Save, SlidersHorizontal, Undo2 } from "lucide-react";
import { useApi } from "../api-context";
import type { RuntimeSettingDto } from "../api/generated";
import { DataTable, Empty, Page, Status } from "../components/ui";

export function SettingsPage() {
  const { api, showToast } = useApi();
  const [section, setSection] = useState("");
  const [clientOverridesOnly, setClientOverridesOnly] = useState(false);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const settings = useQuery({ queryKey: ["runtime-settings"], queryFn: () => api.runtimeSettings() });
  const items: RuntimeSettingDto[] = settings.data?.items ?? [];
  const sections = useMemo(() => Array.from(new Set(items.map((item) => item.section))).sort(), [items]);
  const clientOverrideCount = items.filter((item) => item.isClientOverrideEffective).length;
  const visible = items.filter((item) => (!section || item.section === section) && (!clientOverridesOnly || item.isClientOverrideEffective));
  const setSetting = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => api.setRuntimeSetting(key, value),
    onSuccess: (result) => {
      showToast({ kind: "success", text: result.restartRequired ? "Setting saved. Restart required." : "Setting saved." });
      settings.refetch();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const unsetSetting = useMutation({
    mutationFn: (key: string) => api.unsetRuntimeSetting(key),
    onSuccess: (result) => {
      showToast({ kind: "success", text: result.restartRequired ? "Client override removed. Restart required." : "Client override removed." });
      settings.refetch();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const reload = useMutation({
    mutationFn: () => api.reloadRuntimeSettings(),
    onSuccess: () => {
      showToast({ kind: "success", text: "Runtime settings reloaded." });
      settings.refetch();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  return <Page title="Settings" subtitle="Edit server runtime settings stored in the managed overlay." action={<div className="actions"><button className="secondary" disabled={settings.isFetching} onClick={() => settings.refetch()}><RefreshCw size={16} /> Refresh</button><button className="primary" disabled={reload.isPending} onClick={() => reload.mutate()}><SlidersHorizontal size={16} /> Reload</button></div>}>
    <section className="panel settings-panel">
      <div className="section-head settings-head">
        <div>
          <h2>Runtime settings</h2>
          <p><strong>{clientOverrideCount}</strong> client override{clientOverrideCount === 1 ? "" : "s"} active. Environment or command-line values can mask saved overlay edits.</p>
        </div>
        <div className="settings-filters">
          <label className="settings-section-filter">Section<select value={section} onChange={(event) => setSection(event.target.value)}><option value="">All sections</option>{sections.map((item) => <option key={item} value={item}>{item}</option>)}</select></label>
          <label className="settings-toggle"><input type="checkbox" checked={clientOverridesOnly} onChange={(event) => setClientOverridesOnly(event.target.checked)} /> Client overrides only</label>
        </div>
      </div>
      <DataTable headers={["Setting", "Effective value", "Client override", "Apply", "Source", "Edit", "Actions"]} isLoading={settings.isLoading}>
        {visible.map((item) => {
          const draft = drafts[item.key] ?? item.overlayValue ?? item.effectiveValue ?? "";
          return <tr key={item.key}>
            <td><div className="setting-key"><strong>{item.name}</strong><span>{item.key}</span>{item.warning && <em>{item.warning}</em>}</div></td>
            <td><code>{formatValue(item.effectiveValue)}</code></td>
            <td>{item.hasOverlayValue ? <code>{formatValue(item.overlayValue)}</code> : <span className="muted">None</span>}</td>
            <td><Status value={item.applyMode === "Live" ? "Live" : "Restart required"} /></td>
            <td><Status value={item.overrideStatus} /></td>
            <td><SettingEditor setting={item} value={draft} onChange={(value) => setDrafts((current) => ({ ...current, [item.key]: value }))} /></td>
            <td><div className="actions"><button className="primary" disabled={setSetting.isPending || item.isReadOnly} title="Save setting" onClick={() => setSetting.mutate({ key: item.key, value: draft })}><Save size={15} /></button><button className="secondary" disabled={unsetSetting.isPending || !item.hasOverlayValue} title="Restore default" onClick={() => unsetSetting.mutate(item.key)}><Undo2 size={15} /></button><button className="ghost" title="Reset edit field" onClick={() => setDrafts((current) => ({ ...current, [item.key]: item.overlayValue ?? item.effectiveValue ?? "" }))}><RotateCcw size={15} /></button></div></td>
          </tr>;
        })}
      </DataTable>
      {!settings.isLoading && visible.length === 0 && <Empty text="No settings match the selected filters." />}
    </section>
  </Page>;
}

function SettingEditor({ setting, value, onChange }: { setting: RuntimeSettingDto; value: string; onChange: (value: string) => void }) {
  if (setting.valueType === "Boolean") {
    return <select className="setting-input" value={value || "false"} onChange={(event) => onChange(event.target.value)}><option value="true">true</option><option value="false">false</option></select>;
  }
  if (setting.valueType === "Json") {
    return <textarea className="setting-input setting-json" value={value} onChange={(event) => onChange(event.target.value)} />;
  }
  return <input className="setting-input" type={setting.valueType === "Integer" ? "number" : "text"} value={value} placeholder={placeholder(setting)} onChange={(event) => onChange(event.target.value)} />;
}

function placeholder(setting: RuntimeSettingDto) {
  if (setting.valueType === "TimeSpan") return "00:00:06";
  if (setting.valueType === "DateTimeOffset") return "2026-05-15T10:00:00+00:00";
  return setting.isNullable ? "empty clears to null" : "value";
}

function formatValue(value: string | null | undefined) {
  if (value === null || value === undefined || value === "") return "null";
  return value.length > 96 ? `${value.slice(0, 96)}...` : value;
}
