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
import { ArrowDown, ArrowUp, ArrowUpDown, Save, Search, X } from "lucide-react";

export function Page({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return <div className="page"><div className="page-head"><div><h1>{title}</h1><p>Manage Chobo operations and configuration.</p></div>{action}</div>{children}</div>;
}

export function CrudPage({ title, showForm, onAdd, formTitle, saveLabel = "Save", form, table, onSave, onCancel }: { title: string; showForm: boolean; onAdd: () => void; formTitle?: string; saveLabel?: string; form: ReactNode; table: ReactNode; onSave: () => void; onCancel?: () => void }) {
  return <Page title={title} action={!showForm ? <button className="primary" onClick={onAdd}><Save size={16} /> Add</button> : undefined}><section className="panel">{table}</section>{showForm && <section className="panel form-panel"><div className="section-head">{formTitle && <h2>{formTitle}</h2>}{onCancel && <button className="ghost" onClick={onCancel}>Cancel</button>}</div><div className="form-grid">{form}</div><div className="actions"><button className="primary" onClick={onSave}><Save size={16} /> {saveLabel}</button></div></section>}</Page>;
}

type ParsedCell = { node: ReactNode; text: string; className?: string };
type ParsedRow = { id: string; cells: ParsedCell[] };

export function DataTable({ headers, children }: { headers: string[]; children: ReactNode }) {
  const [sorting, setSorting] = useState<SortingState>([]);
  const [globalFilter, setGlobalFilter] = useState("");
  const [columnFilters, setColumnFilters] = useState<ColumnFiltersState>([]);
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
  }), [headers]);
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

  return <div className="data-grid">
    <div className="grid-toolbar">
      <label className="grid-search" title="Search all columns"><Search size={15} /><input aria-label="Search all columns" placeholder="Search all columns" value={globalFilter} onChange={(event) => setGlobalFilter(event.target.value)} /></label>
      <span className="grid-count">{visibleRows.length} / {data.length}</span>
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
      ? <tr><td colSpan={headers.length}><Empty text={data.length === 0 ? "No rows to display." : "No rows match the current filters."} /></td></tr>
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

export function Empty({ text }: { text: string }) {
  return <div className="empty">{text}</div>;
}

export function Detail({ label, value }: { label: string; value: ReactNode }) {
  return <div><span>{label}</span><strong>{value}</strong></div>;
}

export function Input({ label, value, onChange, type = "text" }: { label: string; value: string; onChange: (value: string) => void; type?: string }) {
  return <label>{label}<input type={type} value={value} onChange={(event) => onChange(event.target.value)} /></label>;
}

export function Select({ label, value, onChange, options }: { label: string; value: string; onChange: (value: string) => void; options: string[][] }) {
  return <label>{label}<select value={value} onChange={(event) => onChange(event.target.value)}><option value="">Select...</option>{options.map(([optionValue, optionLabel]) => <option key={optionValue} value={optionValue}>{optionLabel}</option>)}</select></label>;
}
