import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  Activity,
  Archive,
  CalendarClock,
  Download,
  FileCode2,
  FileClock,
  Github,
  HardDrive,
  History,
  LayoutDashboard,
  RotateCcw,
  Server,
  Settings2,
  Trash2,
  Users
} from "lucide-react";
import { useApi, type Toast } from "../api-context";

const navItems = [
  { to: "/", label: "Dashboard", icon: LayoutDashboard },
  { to: "/backups", label: "Backups", icon: Archive },
  { to: "/restores", label: "Restores", icon: RotateCcw },
  { to: "/policies", label: "Policies", icon: Settings2 },
  { to: "/schedules", label: "Schedules", icon: CalendarClock },
  { to: "/schema", label: "Schema Browser", icon: FileCode2 },
  { to: "/clusters", label: "ClickHouse Clusters", icon: Server },
  { to: "/targets", label: "Backup Storage", icon: HardDrive },
  { to: "/users", label: "Users", icon: Users },
  { to: "/logs", label: "Logs", icon: FileClock },
  { to: "/audit", label: "Audit", icon: History },
  { to: "/gc", label: "Garbage Collector", icon: Trash2 },
  { to: "/import-export", label: "Import/Export", icon: Download },
  { to: "/monitoring", label: "Monitoring", icon: Activity }
];

export function AppShell({ toast, onLogout, children }: { toast: Toast; onLogout: () => void; children: ReactNode }) {
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">C</div>
          <div>
            <strong>Chobo</strong>
            <span>ClickHouse Backups Orchestrator</span>
          </div>
        </div>
        <nav>
          {navItems.map((item) => {
            const Icon = item.icon;
            return (
              <NavLink key={item.to} to={item.to} className={({ isActive }) => `nav-item ${isActive ? "active" : ""}`} end={item.to === "/"}>
                <Icon size={18} />
                <span>{item.label}</span>
              </NavLink>
            );
          })}
        </nav>
        <a className="sidebar-github" href="https://github.com/shahargv/Chobo/" target="_blank" rel="noreferrer" title="Open Chobo on GitHub"><Github size={18} /><span>GitHub</span></a>
      </aside>
      <main className="main">
        <TopBar onLogout={onLogout} />
        {toast && <div className={`toast ${toast.kind}`}>{toast.text}</div>}
        {children}
      </main>
    </div>
  );
}

function TopBar({ onLogout }: { onLogout: () => void }) {
  const { api } = useApi();
  const version = useQuery({ queryKey: ["version"], queryFn: () => api.serverVersion() });
  return (
    <header className="topbar">
      <div>
        <strong>{version.data?.productName ?? "Chobo"}</strong>
        <span>{version.data ? `Product version ${version.data.productVersion}` : "Connecting..."}</span>
      </div>
      <button className="ghost" onClick={onLogout}>Sign out</button>
    </header>
  );
}



