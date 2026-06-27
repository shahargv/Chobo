import { Children, Fragment, isValidElement, useMemo, useState, type ReactElement, type ReactNode } from "react";
import {
  flexRender,
  getCoreRowModel,
  getFacetedRowModel,
  getFacetedUniqueValues,
  getFilteredRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type ColumnFiltersState,
  type Header,
  type SortingState
} from "@tanstack/react-table";
import { AlertTriangle, ArrowDown, ArrowUp, ArrowUpDown, Save, Search, X } from "lucide-react";

export function Page({ title, subtitle, action, children }: { title: string; subtitle: string; action?: ReactNode; children: ReactNode }) {
  return <div className="page"><div className="page-head"><div><h1>{title}</h1><p>{subtitle}</p></div>{action}</div>{children}</div>;
}

export function CrudPage({ title, subtitle, showForm, onAdd, formTitle, saveLabel = "Save", form, table, onSave, onCancel }: { title: string; subtitle: string; showForm: boolean; onAdd: () => void; formTitle?: string; saveLabel?: string; form: ReactNode; table: ReactNode; onSave: () => void; onCancel?: () => void }) {
  return <Page title={title} subtitle={subtitle} action={!showForm ? <button className="primary" onClick={onAdd}><Save size={16} /> Add</button> : undefined}><section className="panel">{table}</section>{showForm && <section className="panel form-panel"><div className="section-head">{formTitle && <h2>{formTitle}</h2>}{onCancel && <button className="ghost" onClick={onCancel}>Cancel</button>}</div><div className="form-grid">{form}</div><div className="actions"><button className="primary" onClick={onSave}><Save size={16} /> {saveLabel}</button></div></section>}</Page>;
}

type ParsedCell = { node: ReactNode; text: string; className?: string };
type ParsedRow = { id: string; cells: ParsedCell[] };

export function DataTable({ headers, children, isLoading = false, loadingText = "Loading rows..." }: { headers: string[]; children: ReactNode; isLoading?: boolean; loadingText?: string }) {
  const [sorting, setSorting] = useState<SortingState>([]);
  const [globalFilter, setGlobalFilter] = useState("");
  const [columnFilters, setColumnFilters] = useState<ColumnFiltersState>([]);
  const headerKey = headers.join("\u001f");
  const data = useMemo(() => parseTableRows(children), [children]);
  const columns = useMemo<ColumnDef<ParsedRow>[]>(() => headers.map((header, index) => {
    const isActionColumn = /^actions$/i.test(header);
    return {
      id: `${header}-${index}`,
      header,
      accessorFn: (row) => row.cells[index]?.text ?? "",
      cell: ({ row }) => row.original.cells[index]?.node ?? null,
      enableSorting: !isActionColumn,
      enableColumnFilter: !isActionColumn,
      enableGlobalFilter: !isActionColumn
    };
  }), [headerKey]);
  const table = useReactTable({
    data,
    columns,
    state: { sorting, globalFilter, columnFilters },
    onSortingChange: setSorting,
    onGlobalFilterChange: setGlobalFilter,
    onColumnFiltersChange: setColumnFilters,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    getFacetedRowModel: getFacetedRowModel(),
    getFacetedUniqueValues: getFacetedUniqueValues(),
    globalFilterFn: "includesString"
  });
  const visibleRows = table.getRowModel().rows;
  const hasFilters = globalFilter.trim().length > 0 || columnFilters.length > 0;
  const emptyText = isLoading ? loadingText : data.length === 0 ? "No rows to display." : "No rows match the current filters.";

  return <div className="data-grid">
    <div className="grid-toolbar">
      <label className="grid-search" title="Search all columns"><Search size={15} /><input aria-label="Search all columns" placeholder="Search all columns" value={globalFilter} onChange={(event) => setGlobalFilter(event.target.value)} /></label>
      <span className="grid-count">{isLoading && data.length === 0 ? "Loading" : `${visibleRows.length} / ${data.length}`}</span>
      {hasFilters && <button className="grid-clear" title="Clear filters" onClick={() => {
        setGlobalFilter("");
        setColumnFilters([]);
      }}><X size={15} /></button>}
    </div>
    <div className="table-wrap"><table><thead>{table.getHeaderGroups().map((headerGroup) => <Fragment key={headerGroup.id}>
      <tr>{headerGroup.headers.map((header) => {
        const sorted = header.column.getIsSorted();
        const canSort = header.column.getCanSort();
        return <th key={header.id}>
          {canSort
            ? <button type="button" className={`sort-header ${sorted ? "active" : ""}`} onClick={header.column.getToggleSortingHandler()} title={`Sort ${String(header.column.columnDef.header)}`}>
              <span>{flexRender(header.column.columnDef.header, header.getContext())}</span>
              {sorted === "asc" ? <ArrowUp size={13} /> : sorted === "desc" ? <ArrowDown size={13} /> : <ArrowUpDown size={13} />}
            </button>
            : <span className="plain-header">{flexRender(header.column.columnDef.header, header.getContext())}</span>}
        </th>;
      })}</tr>
      <tr className="filter-row">{headerGroup.headers.map((header) => <th key={`${header.id}-filter`}>
        {header.column.getCanFilter() && <ColumnFilter header={header} />}
      </th>)}</tr>
    </Fragment>)}</thead><tbody>{visibleRows.length === 0
      ? <tr><td colSpan={headers.length}><Empty text={emptyText} /></td></tr>
      : visibleRows.map((row) => <tr key={row.original.id}>{row.getVisibleCells().map((cell, index) => <td key={cell.id} className={row.original.cells[index]?.className}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>)}</tr>)}</tbody></table></div>
  </div>;
}

function ColumnFilter({ header }: { header: Header<ParsedRow, unknown> }) {
  const filterValue = (header.column.getFilterValue() ?? "") as string;
  const labelText = String(header.column.columnDef.header);
  const normalizedLabel = labelText.toLowerCase();
  const uniqueValues = Array.from(header.column.getFacetedUniqueValues().keys())
    .map((value) => String(value))
    .filter((value) => value.trim().length > 0)
    .sort((left, right) => left.localeCompare(right));
  const isLongTextColumn = /time|created|started|ended|message|details|path|id/.test(normalizedLabel);
  const shouldUseSelect = !isLongTextColumn && uniqueValues.length > 0 && uniqueValues.length <= 12 && uniqueValues.every((value) => value.length <= 48);
  const label = `Filter ${labelText}`;

  if (shouldUseSelect) {
    return <select className="column-filter column-filter-select" aria-label={label} value={filterValue} onChange={(event) => header.column.setFilterValue(event.target.value || undefined)}>
      <option value="">All</option>
      {uniqueValues.map((option) => <option key={option} value={option}>{option}</option>)}
    </select>;
  }

  return <input className="column-filter" aria-label={label} placeholder="Filter" value={filterValue} onChange={(event) => header.column.setFilterValue(event.target.value)} />;
}

function parseTableRows(children: ReactNode): ParsedRow[] {
  return Children.toArray(children).flatMap((rowNode, rowIndex) => {
    if (!isValidElement(rowNode)) return [];
    const row = rowNode as ReactElement<{ children?: ReactNode }>;
    const cells = Children.toArray(row.props.children).map((cellNode) => {
      if (!isValidElement(cellNode)) return { node: cellNode, text: textFromNode(cellNode) };
      const cell = cellNode as ReactElement<{ children?: ReactNode; className?: string }>;
      return { node: cell.props.children, text: textFromNode(cell.props.children), className: cell.props.className };
    });
    return [{ id: row.key?.toString() ?? `${rowIndex}`, cells }];
  });
}

function textFromNode(node: ReactNode): string {
  if (node === null || node === undefined || typeof node === "boolean") return "";
  if (typeof node === "string" || typeof node === "number") return String(node);
  if (Array.isArray(node)) return node.map(textFromNode).join(" ");
  if (isValidElement(node)) {
    const element = node as ReactElement<{ children?: ReactNode; value?: unknown; checked?: boolean }>;
    if (element.props.value !== undefined) return String(element.props.value);
    if (element.props.checked !== undefined) return element.props.checked ? "yes" : "no";
    return textFromNode(element.props.children);
  }
  return "";
}

export function Stat({ label, value, tone }: { label: string; value: number; tone?: "ok" | "warn" | "bad" }) {
  return <div className={`stat ${tone ?? ""}`}><span>{label}</span><strong>{value}</strong></div>;
}

export function Status({ value }: { value: string }) {
  const tone = /failed|delete|error/i.test(value) ? "bad" : /running|queued|partial/i.test(value) ? "warn" : /succeed|enabled|ok/i.test(value) ? "ok" : "";
  return <span className={`status ${tone}`}>{value}</span>;
}

export function Drawer({ title, onClose, children, className }: { title: string; onClose: () => void; children: ReactNode; className?: string }) {
  return <div className="drawer-backdrop" onClick={onClose}><aside className={`drawer ${className ?? ""}`} onClick={(event) => event.stopPropagation()}><div className="drawer-head"><h2>{title}</h2><button className="ghost" onClick={onClose}>Close</button></div>{children}</aside></div>;
}


export function ConfirmDialog({ title, message, confirmLabel, cancelLabel = "Cancel", tone = "danger", busy = false, onConfirm, onCancel }: { title: string; message: string; confirmLabel: string; cancelLabel?: string; tone?: "danger" | "primary"; busy?: boolean; onConfirm: () => void; onCancel: () => void }) {
  return <div className="modal-backdrop" role="presentation" onClick={onCancel}>
    <section className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="confirm-dialog-title" onClick={(event) => event.stopPropagation()}>
      <div className={`confirm-icon ${tone}`}><AlertTriangle size={22} /></div>
      <div className="confirm-content">
        <h2 id="confirm-dialog-title">{title}</h2>
        <p>{message}</p>
        <div className="confirm-actions">
          <button className="ghost" disabled={busy} onClick={onCancel}>{cancelLabel}</button>
          <button className={tone === "danger" ? "danger" : "primary"} disabled={busy} onClick={onConfirm}>{confirmLabel}</button>
        </div>
      </div>
    </section>
  </div>;
}
export function ExpandableErrorText({ text, title = "Failure details", previewLines = 3, previewCharacters = 480 }: { text?: string | null; title?: string; previewLines?: number; previewCharacters?: number }) {
  const [isExpanded, setIsExpanded] = useState(false);
  const normalized = text?.trim();
  if (!normalized) return null;

  const preview = previewText(normalized, previewLines, previewCharacters);
  const hasMore = preview !== normalized;
  return <>
    <span className="error-preview">{preview}</span>
    {hasMore && <button type="button" className="link-button error-expand-button" onClick={() => setIsExpanded(true)}>Expand</button>}
    {isExpanded && <ErrorDetailDialog title={title} error={normalized} onClose={() => setIsExpanded(false)} />}
  </>;
}

export function ErrorDetailDialog({ title, error, onClose }: { title: string; error: string; onClose: () => void }) {
  return <div className="modal-backdrop" role="presentation" onClick={onClose}>
    <section className="error-detail-dialog" role="dialog" aria-modal="true" aria-labelledby="error-detail-title" onClick={(event) => event.stopPropagation()}>
      <div className="section-head"><h2 id="error-detail-title">{title}</h2><button className="ghost" onClick={onClose}>Close</button></div>
      <pre>{error}</pre>
    </section>
  </div>;
}

function previewText(text: string, maxLines: number, maxCharacters: number) {
  const lines = text.split(/\r?\n/);
  const linePreview = lines.length > maxLines ? lines.slice(0, maxLines).join("\n") : text;
  if (linePreview.length > maxCharacters) return `${linePreview.slice(0, maxCharacters).trimEnd()}...`;
  return linePreview === text ? text : `${linePreview}\n...`;
}

export function Empty({ text }: { text: string }) {
  return <div className="empty">{text}</div>;
}

export function Detail({ label, value, className }: { label: string; value: ReactNode; className?: string }) {
  return <div className={className}><span>{label}</span><strong>{value}</strong></div>;
}

export function Input({ label, value, onChange, type = "text", placeholder }: { label: string; value: string; onChange: (value: string) => void; type?: string; placeholder?: string }) {
  return <label>{label}<input type={type} value={value} placeholder={placeholder} onChange={(event) => onChange(event.target.value)} /></label>;
}

export function Select({ label, value, onChange, options }: { label: string; value: string; onChange: (value: string) => void; options: string[][] }) {
  return <label>{label}<select value={value} onChange={(event) => onChange(event.target.value)}><option value="">Select...</option>{options.map(([optionValue, optionLabel]) => <option key={optionValue} value={optionValue}>{optionLabel}</option>)}</select></label>;
}

