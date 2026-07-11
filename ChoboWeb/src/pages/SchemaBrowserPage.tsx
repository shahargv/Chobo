import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Database, Download } from "lucide-react";
import type { SchemaBackupSummaryDto, SchemaDatabaseDto, SchemaTableDto } from "../api/generated";
import { useApi } from "../api-context";
import { Empty, Input, Page, Select, Status } from "../components/ui";
import { formatTime } from "../utils/format";

type RangePreset = "last24h" | "last7d" | "last30d" | "custom";

export function SchemaBrowserPage() {
  const { api, showToast } = useApi();
  const [rangePreset, setRangePreset] = useState<RangePreset>("last7d");
  const [customFrom, setCustomFrom] = useState(() => toDateInput(daysAgo(7)));
  const [customTo, setCustomTo] = useState(() => toDateInput(new Date()));
  const range = useMemo(() => buildRange(rangePreset, customFrom, customTo), [rangePreset, customFrom, customTo]);
  const backups = useQuery({ queryKey: ["schema-backups", range.from, range.to], queryFn: () => api.schemaBackups(range) });
  const [selectedBackupId, setSelectedBackupId] = useState("");
  const [selectedDatabase, setSelectedDatabase] = useState<string | null>(null);
  const [selectedTableId, setSelectedTableId] = useState<string | null>(null);
  const [tableFilter, setTableFilter] = useState("");

  useEffect(() => {
    setSelectedBackupId("");
    setSelectedDatabase(null);
    setSelectedTableId(null);
    setTableFilter("");
  }, [range.from, range.to]);

  const schema = useQuery({
    queryKey: ["schema-backup", selectedBackupId],
    queryFn: () => api.backupSchema(selectedBackupId),
    enabled: selectedBackupId.length > 0
  });
  const databases = schema.data?.databases ?? [];
  const normalizedTableFilter = tableFilter.trim().toLowerCase();
  const filteredDatabases = useMemo(
    () => filterDatabases(databases, normalizedTableFilter),
    [databases, normalizedTableFilter]
  );
  const currentDatabase = filteredDatabases.find((item) => item.database === selectedDatabase) ?? filteredDatabases[0] ?? null;
  const currentTable = useMemo(() => {
    const tables = filteredDatabases.flatMap((database) => database.tables);
    return tables.find((table) => table.backupTableId === selectedTableId) ?? currentDatabase?.tables[0] ?? null;
  }, [currentDatabase, filteredDatabases, selectedTableId]);

  useEffect(() => {
    if (!currentDatabase) {
      if (selectedDatabase !== null) setSelectedDatabase(null);
      if (selectedTableId !== null) setSelectedTableId(null);
      return;
    }
    if (selectedDatabase !== currentDatabase.database) {
      setSelectedDatabase(currentDatabase.database);
    }
    if ((currentTable?.backupTableId ?? null) !== selectedTableId) {
      setSelectedTableId(currentTable?.backupTableId ?? null);
    }
  }, [currentDatabase, currentTable, selectedDatabase, selectedTableId]);

  const selectedBackup = backups.data?.find((backup) => backup.id === selectedBackupId);
  const exportSchema = useMutation({
    mutationFn: ({ backupId, database }: { backupId: string; database?: string }) => api.exportBackupSchema(backupId, database),
    onSuccess: (text, variables) => {
      const suffix = variables.database ? `-${variables.database}` : "";
      downloadText(`chobo-schema-${variables.backupId}${suffix}.sql`, text);
      showToast({ kind: "success", text: "Schema export downloaded." });
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  return (
    <Page title="Schema Browser" subtitle="Browse and export table schemas captured during backup runs.">
      <section className="panel">
        <div className="form-grid">
          <Select label="Backup range" value={rangePreset} onChange={(value) => setRangePreset(value as RangePreset)} options={[["last7d", "Last week"], ["last24h", "Last 24 hours"], ["last30d", "Last 30 days"], ["custom", "Custom range"]]} />
          {rangePreset === "custom" && <label>From<input type="date" value={customFrom} onChange={(event) => setCustomFrom(event.target.value)} /></label>}
          {rangePreset === "custom" && <label>To<input type="date" value={customTo} onChange={(event) => setCustomTo(event.target.value)} /></label>}
          <Select label="Backup" value={selectedBackupId} onChange={(value) => { setSelectedBackupId(value); setSelectedDatabase(null); setSelectedTableId(null); setTableFilter(""); }} options={(backups.data ?? []).map((backup) => [backup.id, formatBackupOption(backup)])} />
          {selectedBackup && <div className="detail-list compact">
            <div><span>Status</span><strong><Status value={selectedBackup.status} /></strong></div>
            <div><span>Source cluster</span><strong>{selectedBackup.sourceClusterName}</strong></div>
            <div><span>Policy</span><strong>{selectedBackup.policyName ?? "Manual"}</strong></div>
            <div><span>Mode</span><strong>{selectedBackup.contentMode === "SchemaOnly" ? "Schema only" : "Schema + data"}</strong></div>
            <div><span>Backup type</span><strong>{selectedBackup.backupType}</strong></div>
          </div>}
        </div>
        {!backups.isLoading && (backups.data ?? []).length === 0 && <Empty text="No backup runs with schema metadata are available in this range." />}
      </section>

      {selectedBackupId && <section className="panel schema-browser-grid">
        <div className="schema-tree">
          <div className="section-head"><h2>Objects</h2><Database size={18} /></div>
          <Input label="Filter tables" value={tableFilter} onChange={setTableFilter} placeholder="Contains..." />
          {schema.isLoading && <Empty text="Loading schema metadata." />}
          {schema.error && <Empty text={String(schema.error)} />}
          {!schema.isLoading && !schema.error && databases.length === 0 && <Empty text="This backup has no retained schema metadata." />}
          {!schema.isLoading && !schema.error && databases.length > 0 && filteredDatabases.length === 0 && <Empty text="No tables match this filter." />}
          <div className="schema-object-list">
            {filteredDatabases.map((database) => (
              <div className="schema-database" key={database.database}>
                <button className={database.database === currentDatabase?.database ? "tree-node active" : "tree-node"} onClick={() => { setSelectedDatabase(database.database); setSelectedTableId(database.tables[0]?.backupTableId ?? null); }}>
                  {database.database}
                </button>
                {database.database === currentDatabase?.database && <div className="schema-table-list">
                  {database.tables.map((table) => <button key={table.backupTableId} className={table.backupTableId === currentTable?.backupTableId ? "tree-node table active" : "tree-node table"} onClick={() => setSelectedTableId(table.backupTableId)}>{table.table}</button>)}
                </div>}
              </div>
            ))}
          </div>
        </div>
        <div className="schema-viewer">
          <div className="section-head">
            <div>
              <h2>{currentTable ? `${currentTable.database}.${currentTable.table}` : "Schema SQL"}</h2>
              {currentTable && <span className="hint">{currentTable.engine} · data {currentTable.dataBackedUp ? "backed up" : "not backed up"}</span>}
            </div>
            <div className="actions">
              <button className="secondary" disabled={!selectedBackupId || exportSchema.isPending} onClick={() => exportSchema.mutate({ backupId: selectedBackupId })}><Download size={16} /> Export all</button>
              <button className="secondary" disabled={!selectedBackupId || !currentDatabase || exportSchema.isPending} onClick={() => exportSchema.mutate({ backupId: selectedBackupId, database: currentDatabase?.database })}><Download size={16} /> Export database</button>
            </div>
          </div>
          {currentTable ? <SchemaSql table={currentTable} /> : <Empty text="Choose a backup and table to view its captured CREATE TABLE statement." />}
        </div>
      </section>}
    </Page>
  );
}

function SchemaSql({ table }: { table: SchemaTableDto }) {
  const [formattedSql, setFormattedSql] = useState(table.createTableSql);

  useEffect(() => {
    let isCurrent = true;
    setFormattedSql(table.createTableSql);
    void formatSchemaSql(table.createTableSql).then((sql) => {
      if (isCurrent) setFormattedSql(sql);
    });
    return () => { isCurrent = false; };
  }, [table.createTableSql]);

  return <pre className="schema-sql"><code>{formattedSql}</code></pre>;
}

function filterDatabases(databases: SchemaDatabaseDto[], tableFilter: string) {
  if (!tableFilter) {
    return databases;
  }

  return databases
    .map((database) => ({
      ...database,
      tables: database.tables.filter((table) => table.table.toLowerCase().includes(tableFilter))
    }))
    .filter((database) => database.tables.length > 0);
}

function formatBackupOption(backup: SchemaBackupSummaryDto) {
  const policy = backup.policyName ? ` - ${backup.policyName}` : " - Manual";
  const mode = backup.contentMode === "SchemaOnly" ? "schema only" : "schema + data";
  return `${formatTime(backup.createdAt)} - ${backup.sourceClusterName}${policy} - ${mode} - ${backup.tableCount} table(s)`;
}

async function formatSchemaSql(sql: string) {
  try {
    const { format } = await import("sql-formatter");
    return format(sql, { language: "clickhouse", tabWidth: 2, expressionWidth: 40 });
  } catch {
    return sql;
  }
}

function downloadText(fileName: string, text: string) {
  const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  link.click();
  URL.revokeObjectURL(url);
}

function buildRange(preset: RangePreset, customFrom: string, customTo: string) {
  if (preset === "custom") {
    return {
      from: customFrom ? new Date(`${customFrom}T00:00:00`).toISOString() : undefined,
      to: customTo ? new Date(`${customTo}T23:59:59.999`).toISOString() : undefined
    };
  }

  const now = new Date();
  const from = preset === "last24h" ? hoursAgo(24) : preset === "last30d" ? daysAgo(30) : daysAgo(7);
  return { from: from.toISOString(), to: now.toISOString() };
}

function hoursAgo(hours: number) {
  const date = new Date();
  date.setHours(date.getHours() - hours);
  return date;
}

function daysAgo(days: number) {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return date;
}

function toDateInput(date: Date) {
  return date.toISOString().slice(0, 10);
}
