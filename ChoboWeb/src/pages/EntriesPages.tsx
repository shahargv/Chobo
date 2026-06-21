import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronsLeft, ChevronLeft, ChevronRight, ChevronsRight, RefreshCw } from "lucide-react";
import type { AuditEntryDto, PagedResultDto } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Input, Page } from "../components/ui";

type EntryPageState = { offset: number; limit: number; startTime: string; endTime: string };
type EntryQueryParams = { startTime?: string; endTime?: string; offset: number; limit: number };
type EntryCell = string | { text: string; className?: string };

export function Logs() {
  const { api, showToast } = useApi();
  const [page, setPage] = useState<EntryPageState>(() => defaultEntryPageState());
  const queryParams = entryQueryParams(page);
  const logs = useQuery({ queryKey: ["logs", queryParams], queryFn: () => api.logs(queryParams) });
  useEffect(() => setPage((current) => ({ ...current, offset: 0 })), [page.startTime, page.endTime, page.limit]);
  return <EntriesPage
    title="Logs"
    page={page}
    setPage={setPage}
    result={logs.data}
    isFetching={logs.isFetching}
    onRefresh={() => logs.refetch()}
    onClear={(before) => api.clearLogs(before).then(() => showToast({ kind: "success", text: "Logs cleared." })).then(() => logs.refetch())}
    headers={["Time", "Level", "Category", "Message", "Exception details"]}
    rows={(logs.data?.items ?? []).map((log) => [
      formatTimeSeconds(log.timestamp),
      log.level,
      log.category,
      log.message,
      { text: log.exception ?? "", className: "mono wide-cell" }
    ])} />;
}

export function Audit() {
  const { api, showToast } = useApi();
  const [page, setPage] = useState<EntryPageState>(() => defaultEntryPageState());
  const queryParams = entryQueryParams(page);
  const audits = useQuery({ queryKey: ["audit", queryParams], queryFn: () => api.audits(queryParams) });
  useEffect(() => setPage((current) => ({ ...current, offset: 0 })), [page.startTime, page.endTime, page.limit]);
  return <EntriesPage
    title="Audit"
    page={page}
    setPage={setPage}
    result={audits.data}
    isFetching={audits.isFetching}
    onRefresh={() => audits.refetch()}
    onClear={(before) => api.clearAudits(before).then(() => showToast({ kind: "success", text: "Audit cleared." })).then(() => audits.refetch())}
    headers={["Time", "Actor", "Action", "Entity", "Details"]}
    rows={(audits.data?.items ?? []).map((audit: AuditEntryDto) => [formatTimeSeconds(audit.timestamp), audit.actorName, audit.action, `${audit.entityType}:${audit.entityId ?? ""}`, JSON.stringify(audit.details)])} />;
}

function EntriesPage({ title, page, setPage, result, isFetching, onRefresh, onClear, headers, rows }: { title: string; page: EntryPageState; setPage: (updater: EntryPageState | ((current: EntryPageState) => EntryPageState)) => void; result?: PagedResultDto<unknown>; isFetching: boolean; onRefresh: () => void; onClear: (before: string) => void; headers: string[]; rows: EntryCell[][] }) {
  const totalCount = result?.totalCount ?? 0;
  const limit = result?.limit ?? page.limit;
  const offset = result?.offset ?? page.offset;
  const firstRow = totalCount === 0 ? 0 : offset + 1;
  const lastRow = Math.min(offset + rows.length, totalCount);
  const lastOffset = Math.max(0, Math.floor(Math.max(totalCount - 1, 0) / limit) * limit);
  const canMoveBack = offset > 0;
  const canMoveForward = offset + limit < totalCount;
  const pageText = totalCount === 0 ? "No rows" : `${firstRow}-${lastRow} of ${totalCount}`;
  const updatePage = (patch: Partial<EntryPageState>) => setPage((current) => ({ ...current, ...patch }));
  const moveTo = (nextOffset: number) => setPage((current) => ({ ...current, offset: Math.max(0, nextOffset) }));

  return <Page title={title} subtitle={title === "Logs" ? "Search application logs with time windows, paging, and operation filters." : "Review audited configuration and operational changes with paging and filters."}>
    <section className="panel entries-panel">
      <div className="entry-filter-bar">
        <Input label="Start time" type="datetime-local" value={page.startTime} onChange={(value) => updatePage({ startTime: value })} />
        <Input label="End time" type="datetime-local" value={page.endTime} onChange={(value) => updatePage({ endTime: value })} />
        <Input label="Page size" type="number" value={`${page.limit}`} onChange={(value) => updatePage({ limit: Math.max(1, Number(value) || 200) })} />
        <button className="secondary" disabled={isFetching} onClick={onRefresh}><RefreshCw size={16} /> Refresh</button>
      </div>
      <DataTable headers={headers} isLoading={isFetching && rows.length === 0}>
        {rows.map((row, index) => <tr key={offset + index}>{row.map((cell, cellIndex) => {
          const value = typeof cell === "string" ? cell : cell.text;
          const className = typeof cell === "string" ? undefined : cell.className;
          return <td key={headers[cellIndex] ?? cellIndex} className={className}>{value}</td>;
        })}</tr>)}
      </DataTable>
      <div className="entry-page-bar">
        <div className="pager-buttons">
          <button className="secondary icon-button" title="First page" disabled={!canMoveBack} onClick={() => moveTo(0)}><ChevronsLeft size={16} /></button>
          <button className="secondary icon-button" title="Previous page" disabled={!canMoveBack} onClick={() => moveTo(offset - limit)}><ChevronLeft size={16} /></button>
          <button className="secondary icon-button" title="Next page" disabled={!canMoveForward} onClick={() => moveTo(offset + limit)}><ChevronRight size={16} /></button>
          <button className="secondary icon-button" title="Last page" disabled={!canMoveForward} onClick={() => moveTo(lastOffset)}><ChevronsRight size={16} /></button>
        </div>
        <span className="grid-count">{pageText}</span>
        <button className="danger" onClick={() => onClear(new Date().toISOString())}>Clear before now</button>
      </div>
    </section>
  </Page>;
}

function defaultEntryPageState(): EntryPageState {
  const end = new Date();
  const start = new Date(end.getTime() - 60 * 60 * 1000);
  return { offset: 0, limit: 200, startTime: toDateTimeLocalValue(start), endTime: toDateTimeLocalValue(end) };
}

function entryQueryParams(page: EntryPageState): EntryQueryParams {
  return {
    startTime: localDateTimeToIso(page.startTime),
    endTime: localDateTimeToIso(page.endTime),
    offset: page.offset,
    limit: page.limit
  };
}

function toDateTimeLocalValue(value: Date) {
  const pad = (part: number) => `${part}`.padStart(2, "0");
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}T${pad(value.getHours())}:${pad(value.getMinutes())}`;
}

function localDateTimeToIso(value: string) {
  if (!value) return undefined;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

function formatTimeSeconds(value?: string | null) {
  if (!value) return "never";
  return new Date(value).toLocaleString(undefined, {
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit"
  });
}




