import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  Archive,
  CalendarClock,
  Download,
  FileClock,
  HardDrive,
  History,
  LayoutDashboard,
  RotateCcw,
  Server,
  Settings2,
  Users
} from "lucide-react";
import { useApi, type Toast } from "../api-context";

const navItems = [
  { to: "/", label: "Dashboard", icon: LayoutDashboard },
  { to: "/backups", label: "Backups", icon: Archive },
  { to: "/restores", label: "Restores", icon: RotateCcw },
  { to: "/policies", label: "Policies", icon: Settings2 },
  { to: "/schedules", label: "Schedules", icon: CalendarClock },
  { to: "/clusters", label: "ClickHouse Clusters", icon: Server },
  { to: "/targets", label: "Backup Storage", icon: HardDrive },
  { to: "/users", label: "Users", icon: Users },
  { to: "/logs", label: "Logs", icon: FileClock },
  { to: "/audit", label: "Audit", icon: History },
  { to: "/import-export", label: "Import/Export", icon: Download }
];

export function AppShell({ toast, onLogout, children }: { toast: Toast; onLogout: () => void; children: ReactNode }) {
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">C</div>
          <div>
            <strong>Chobo</strong>
            <span>ClickHouse Backup Orchestrator</span>
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
        <span>{version.data ? `API v${version.data.apiVersion} · schema ${version.data.databaseSchemaVersion}` : "Connecting..."}</span>
      </div>
      <button className="ghost" onClick={onLogout}>Sign out</button>
    </header>
  );
}
