import { useEffect, useMemo, useState } from "react";
import { ChevronDown, ChevronRight, Plus, Trash2 } from "lucide-react";

export type ClickHouseSettingValue = string | number | boolean;
export type ClickHouseSettings = Record<string, ClickHouseSettingValue>;
export type ClickHouseSettingSource = { name: string; value: ClickHouseSettingValue; source: string };

type Row = { key: string; type: "string" | "number" | "boolean"; value: string; source?: string };

const reserved = new Set(["base_backup", "allow_non_empty_tables"]);
const emptySources: ClickHouseSettingSource[] = [];

export function ClickHouseAdvancedSettingsEditor({ title, value, sources = emptySources, onChange, defaultOpen = false, onValidityChange }: { title: string; value: ClickHouseSettings; sources?: ClickHouseSettingSource[]; onChange: (value: ClickHouseSettings) => void; defaultOpen?: boolean; onValidityChange?: (isValid: boolean) => void }) {
  const [isOpen, setIsOpen] = useState(defaultOpen);
  const sourcesKey = JSON.stringify(sources);
  const sourceByName = useMemo(() => new Map(sources.map((source) => [source.name.toLowerCase(), source.source])), [sourcesKey]);
  const externalRows = useMemo(() => Object.entries(value ?? {}).sort(([a], [b]) => a.localeCompare(b)).map(([key, raw]) => toRow(key, raw, sourceByName.get(key.toLowerCase()))), [value, sourcesKey]);
  const [rows, setRows] = useState<Row[]>(externalRows);
  const errors = validateRows(rows);
  const rowsSettingsKey = JSON.stringify(fromRows(rows));
  const externalSettingsKey = JSON.stringify(fromRows(externalRows));
  useEffect(() => {
    if (rowsSettingsKey !== externalSettingsKey) setRows(externalRows);
  }, [externalRows, externalSettingsKey, rowsSettingsKey]);
  useEffect(() => { onValidityChange?.(errors.length === 0); }, [errors.length, onValidityChange]);
  const updateRows = (next: Row[]) => {
    setRows(next);
    onChange(fromRows(next));
  };
  const updateRow = (index: number, patch: Partial<Row>) => updateRows(rows.map((row, i) => i === index ? { ...row, ...patch } : row));
  const addRow = () => {
    setIsOpen(true);
    setRows([...rows, { key: "", type: "number", value: "" }]);
  };
  const sourceSummary = [...new Set(rows.map((row) => row.source).filter(Boolean))].join(", ");
  return <div className="clickhouse-settings-editor field-wide">
    <div className="section-head compact-head">
      <button type="button" className="ghost disclosure-button" aria-expanded={isOpen} onClick={() => setIsOpen((open) => !open)}>
        {isOpen ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
        <span>{title}</span>
        <span className="chip">{rows.length} setting{rows.length === 1 ? "" : "s"}</span>
        {sourceSummary && <span className="hint">from {sourceSummary}</span>}
      </button>
      <button type="button" className="secondary" onClick={addRow}><Plus size={16} /> Add setting</button>
    </div>
    {isOpen && <>
      {rows.length === 0 && <span className="hint">No custom ClickHouse settings.</span>}
      {rows.map((row, index) => <div className="settings-row" key={`${row.key}-${index}`}>
        <label>Setting<input value={row.key} onChange={(event) => updateRow(index, { key: event.target.value })} /></label>
        <label>Type<select value={row.type} onChange={(event) => updateRow(index, { type: event.target.value as Row["type"], value: event.target.value === "boolean" ? "true" : row.value })}>
          <option value="number">number</option><option value="string">string</option><option value="boolean">boolean</option>
        </select></label>
        {row.type === "boolean"
          ? <label>Value<select value={row.value} onChange={(event) => updateRow(index, { value: event.target.value })}><option value="true">true</option><option value="false">false</option></select></label>
          : <label>Value<input value={row.value} onChange={(event) => updateRow(index, { value: event.target.value })} /></label>}
        <span className="chip">{row.source ?? "operation"}</span>
        <button type="button" className="ghost icon-button" title="Remove setting" aria-label={`Remove ${row.key || "setting"}`} onClick={() => updateRows(rows.filter((_, i) => i !== index))}><Trash2 size={16} /></button>
      </div>)}
      {errors.map((error) => <span className="field-error" key={error}>{error}</span>)}
    </>}
  </div>;
}

function toRow(key: string, value: ClickHouseSettingValue, source?: string): Row {
  const type = typeof value === "boolean" ? "boolean" : typeof value === "number" ? "number" : "string";
  return { key, type, value: String(value), source };
}

function fromRows(rows: Row[]): ClickHouseSettings {
  const settings: ClickHouseSettings = {};
  for (const row of rows) {
    if (!row.key.trim()) continue;
    const key = row.key.trim();
    settings[key] = row.type === "boolean" ? row.value === "true" : row.type === "number" ? Number(row.value) : row.value;
  }
  return settings;
}

function validateRows(rows: Row[]) {
  const errors: string[] = [];
  const seen = new Set<string>();
  for (const row of rows) {
    const key = row.key.trim();
    if (!key) { errors.push("Setting name is required."); continue; }
    const normalized = key.toLowerCase();
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) errors.push(`${key} is not a valid ClickHouse setting name.`);
    if (reserved.has(normalized)) errors.push(`${key} is managed by Chobo and cannot be set.`);
    if (seen.has(normalized)) errors.push(`${key} is duplicated.`);
    seen.add(normalized);
    if (row.type === "number" && !Number.isFinite(Number(row.value))) errors.push(`${key} requires a numeric value.`);
  }
  return [...new Set(errors)];
}