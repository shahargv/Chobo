import { useMemo, useState } from "react";
import { Route, Routes } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { ChoboApiClient } from "./api/client";
import { ApiContext, type Toast } from "./api-context";
import { AppShell } from "./components/AppShell";
import { LoginScreen } from "./components/LoginScreen";
import { Backups } from "./pages/BackupsPage";
import { Dashboard } from "./pages/DashboardPage";
import { Audit, Logs } from "./pages/EntriesPages";
import { RestoreDetailPage, RestoreHistory, RestoreWizard } from "./pages/RestoresPage";
import { Policies } from "./pages/PoliciesPage";
import { Schedules } from "./pages/SchedulesPage";
import { Clusters } from "./pages/ClustersPage";
import { Targets } from "./pages/TargetsPage";
import { UsersPage } from "./pages/UsersPage";
import { ImportExport } from "./pages/ImportExportPage";
import { GarbageCollectorPage } from "./pages/GarbageCollectorPage";
import { MonitoringPage } from "./pages/MonitoringPage";
import { clearAuth, readStoredAuth, storeAuth } from "./auth";

export function App() {
  const [auth, setAuth] = useState(() => readStoredAuth());
  const [toast, setToast] = useState<Toast>(null);
  const queryClient = useQueryClient();
  const api = useMemo(
    () => new ChoboApiClient(
      () => auth?.token ?? null,
      () => {
        clearAuth();
        setAuth(null);
        queryClient.clear();
      }
    ),
    [auth, queryClient]
  );

  if (!auth) {
    return <LoginScreen onLogin={(token, remembered) => {
      storeAuth(token, remembered);
      setAuth({ token: token.trim(), remembered });
    }} />;
  }

  const showToast = (next: Toast) => {
    setToast(next);
    if (next) window.setTimeout(() => setToast(null), 4500);
  };

  const logout = () => {
    clearAuth();
    setAuth(null);
    queryClient.clear();
  };

  return (
    <ApiContext.Provider value={{ api, showToast }}>
      <AppShell toast={toast} onLogout={logout}>
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/backups" element={<Backups />} />
          <Route path="/backups/:backupId" element={<Backups />} />
          <Route path="/restores" element={<RestoreHistory />} />
          <Route path="/restores/start" element={<RestoreWizard />} />
          <Route path="/restores/:restoreId" element={<RestoreDetailPage />} />
          <Route path="/policies" element={<Policies />} />
          <Route path="/policies/:policyId" element={<Policies />} />
          <Route path="/schedules" element={<Schedules />} />
          <Route path="/schedules/:scheduleId" element={<Schedules />} />
          <Route path="/clusters" element={<Clusters />} />
          <Route path="/clusters/:clusterId" element={<Clusters />} />
          <Route path="/targets" element={<Targets />} />
          <Route path="/users" element={<UsersPage />} />
          <Route path="/logs" element={<Logs />} />
          <Route path="/audit" element={<Audit />} />
          <Route path="/import-export" element={<ImportExport />} />
          <Route path="/monitoring" element={<MonitoringPage />} />
          <Route path="/gc" element={<GarbageCollectorPage />} />
        </Routes>
      </AppShell>
    </ApiContext.Provider>
  );
}






