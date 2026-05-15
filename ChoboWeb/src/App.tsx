import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { NavLink, Route, Routes, useLocation, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Activity,
  Archive,
  CalendarClock,
  Database,
  Download,
  FileClock,
  HardDrive,
  History,
  KeyRound,
  LayoutDashboard,
  ListFilter,
  Play,
  RotateCcw,
  Save,
  Server,
  Settings2,
  ShieldCheck,
  Upload,
  Users,
  CheckCircle2,
  Circle
} from "lucide-react";
import { ChoboApiClient } from "./api/client";
import type {
  AccessTokenDto,
  AuditEntryDto,
  BackupDto,
  BackupPolicyDto,
  BackupScheduleDto,
  BackupTargetDto,
  BackupType,
  ClusterDto,
  CreateAccessTokenResponse,
  CreateUserResponse,
  FailedBackupRetentionMode,
  InitiateRestoreRequest,
  PolicyMatchKind,
  PolicySelector,
  PolicySelectorAction,
  PolicySelectorRule,
  RestoreLayout,
  UpsertClusterRequest,
  UpsertPolicyRequest,
  UpsertS3TargetRequest,
  UpsertScheduleRequest,
  UserDto
} from "./api/generated";
import { clearAuth, readStoredAuth, storeAuth } from "./auth";
import { buildCron, dayOptions, defaultScheduleDraft, parseCronToDraft, ScheduleDraft, summarizeSchedule } from "./schedule";
import { emptySelector } from "./policies";

type Toast = { kind: "success" | "error"; text: string } | null;

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
      }
    ),
    [auth]
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

  return (
    <ApiContext.Provider value={{ api, showToast }}>
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
          <TopBar onLogout={() => {
            clearAuth();
            setAuth(null);
            queryClient.clear();
          }} />
          {toast && <div className={`toast ${toast.kind}`}>{toast.text}</div>}
          <Routes>
            <Route path="/" element={<Dashboard />} />
            <Route path="/backups" element={<Backups />} />
            <Route path="/restores" element={<Restores />} />
            <Route path="/policies" element={<Policies />} />
            <Route path="/schedules" element={<Schedules />} />
            <Route path="/clusters" element={<Clusters />} />
            <Route path="/targets" element={<Targets />} />
            <Route path="/users" element={<UsersPage />} />
            <Route path="/logs" element={<Logs />} />
            <Route path="/audit" element={<Audit />} />
            <Route path="/import-export" element={<ImportExport />} />
          </Routes>
        </main>
      </div>
    </ApiContext.Provider>
  );
}

const ApiContext = createContext<{ api: ChoboApiClient; showToast: (toast: Toast) => void } | null>(null);

function useApi() {
  const context = useContext(ApiContext);
  if (!context) throw new Error("Missing API context.");
  return context;
}

function LoginScreen({ onLogin }: { onLogin: (token: string, remembered: boolean) => void }) {
  const [token, setToken] = useState("");
  const [remembered, setRemembered] = useState(false);
  return (
    <div className="login-screen">
      <form className="login-panel" onSubmit={(event) => {
        event.preventDefault();
        if (token.trim()) onLogin(token, remembered);
      }}>
        <div className="brand-mark large">C</div>
        <h1>Chobo</h1>
        <p>Paste an existing Chobo access token to manage backups, restores, policies, and schedules.</p>
        <label>
          Access token
          <input value={token} onChange={(event) => setToken(event.target.value)} type="password" autoFocus />
        </label>
        <label className="checkbox-row">
          <input type="checkbox" checked={remembered} onChange={(event) => setRemembered(event.target.checked)} />
          Remember this browser
        </label>
        <button className="primary" type="submit"><KeyRound size={16} /> Sign in</button>
      </form>
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

function Dashboard() {
  const { api } = useApi();
  const dashboard = useQuery({ queryKey: ["dashboard"], queryFn: () => api.dashboard(12) });
  const metrics = useQuery({ queryKey: ["metrics"], queryFn: () => api.metrics() });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const allSchedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const restores = useQuery({ queryKey: ["restores"], queryFn: () => api.restores() });
  const running = dashboard.data?.runningBackups ?? [];
  const schedules = dashboard.data?.schedules ?? [];
  const failures = schedules.filter((schedule) => schedule.lastRunFailureReason);
  const onboarding = buildOnboardingSteps({
    clusterCount: clusters.data?.filter((x) => !x.isDeleted).length ?? 0,
    targetCount: targets.data?.filter((x) => !x.isDeleted).length ?? 0,
    policyCount: policies.data?.filter((x) => !x.isDeleted).length ?? 0,
    scheduleCount: allSchedules.data?.filter((x) => !x.isDeleted).length ?? 0,
    backupCount: backups.data?.length ?? 0,
    restoreCount: restores.data?.length ?? 0
  });
  const missingOnboarding = onboarding.filter((step) => !step.done);
  return (
    <Page title="Dashboard" action={<button className="secondary" onClick={() => dashboard.refetch()}><Activity size={16} /> Refresh</button>}>
      {missingOnboarding.length > 0 ? <OnboardingPanel steps={onboarding} /> : <OnboardingComplete />}
      <div className="stat-grid">
        <Stat label="Running backups" value={running.length} tone={running.length ? "warn" : "ok"} />
        <Stat label="Enabled schedules" value={schedules.filter((x) => x.isEnabled).length} />
        <Stat label="Recent failures" value={failures.length} tone={failures.length ? "bad" : "ok"} />
        <Stat label="Metrics" value={Object.keys(metrics.data ?? {}).length} />
      </div>
      <section className="panel">
        <h2>Running backups</h2>
        <DataTable headers={["Status", "Policy", "Started", "Tables", "Shards", "Failure"]}>
          {running.map((backup) => (
            <tr key={backup.backupId}>
              <td><Status value={backup.status} /></td>
              <td>{backup.policyName ?? backup.policyId ?? "manual"}</td>
              <td>{formatTime(backup.startedAt)}</td>
              <td>{backup.tableCount}</td>
              <td>{backup.succeededShardCount}/{backup.shardCount} ok · {backup.runningShardCount} running</td>
              <td>{backup.failureReason ?? ""}</td>
            </tr>
          ))}
        </DataTable>
      </section>
      <section className="panel two-col">
        <div>
          <h2>Schedules</h2>
          <DataTable headers={["Name", "Policy", "Type", "Last run", "Next window"]}>
            {schedules.map((schedule) => (
              <tr key={schedule.scheduleId}>
                <td>{schedule.scheduleName}</td>
                <td>{schedule.policyName ?? schedule.policyId}</td>
                <td>{schedule.backupType}</td>
                <td><Status value={schedule.lastRunStatus ?? "never"} /></td>
                <td>{dashboard.data?.futureSchedules.filter((x) => x.scheduleId === schedule.scheduleId).length ?? 0} planned</td>
              </tr>
            ))}
          </DataTable>
        </div>
        <div>
          <h2>Recent failures</h2>
          <div className="stack">
            {failures.length === 0 ? <Empty text="No schedule failures in the current dashboard window." /> : failures.map((failure) => (
              <div className="summary-row" key={failure.scheduleId}>
                <Status value={failure.lastRunStatus ?? "Failed"} />
                <div><strong>{failure.scheduleName}</strong><span>{failure.lastRunFailureReason}</span></div>
              </div>
            ))}
          </div>
        </div>
      </section>
    </Page>
  );
}

interface OnboardingInput {
  clusterCount: number;
  targetCount: number;
  policyCount: number;
  scheduleCount: number;
  backupCount: number;
  restoreCount: number;
}

interface OnboardingStep {
  number: number;
  title: string;
  body: string;
  to: string;
  action: string;
  done: boolean;
}

function buildOnboardingSteps(input: OnboardingInput): OnboardingStep[] {
  return [
    {
      number: 1,
      title: "Configure ClickHouse clusters",
      body: "Add the ClickHouse source or target clusters Chobo can connect to.",
      to: "/clusters",
      action: "Add cluster",
      done: input.clusterCount > 0
    },
    {
      number: 2,
      title: "Configure backup storage",
      body: "Create S3-compatible storage for backup files.",
      to: "/targets",
      action: "Add storage",
      done: input.targetCount > 0
    },
    {
      number: 3,
      title: "Configure a policy",
      body: "Choose the source cluster, backup storage, selector rules, and retention.",
      to: "/policies",
      action: "Create policy",
      done: input.policyCount > 0
    },
    {
      number: 4,
      title: "Create a schedule",
      body: "Use presets like daily, weekly, or selected days to automate backups.",
      to: "/schedules",
      action: "Create schedule",
      done: input.scheduleCount > 0
    },
    {
      number: 5,
      title: "Execute the first backup",
      body: "Run or wait for a first backup so there is recovery data to inspect.",
      to: "/backups",
      action: "View backups",
      done: input.backupCount > 0
    },
    {
      number: 6,
      title: "Practice a restore",
      body: "Start a restore from a backup to validate the recovery workflow.",
      to: "/restores",
      action: "Start restore",
      done: input.restoreCount > 0
    }
  ];
}

function OnboardingPanel({ steps }: { steps: OnboardingStep[] }) {
  const next = steps.find((step) => !step.done);
  const completed = steps.filter((step) => step.done).length;
  return (
    <section className="panel onboarding-panel">
      <div className="section-head">
        <div>
          <h2>Finish onboarding</h2>
          <p>{completed} of {steps.length} steps complete. Next: {next?.title ?? "all done"}.</p>
        </div>
        {next && <NavLink className="primary" to={next.to}>{next.action}</NavLink>}
      </div>
      <div className="onboarding-list">
        {steps.map((step) => (
          <NavLink key={step.number} to={step.to} className={`onboarding-step ${step.done ? "done" : ""} ${next?.number === step.number ? "next" : ""}`}>
            <div className="step-icon">{step.done ? <CheckCircle2 size={20} /> : <Circle size={20} />}</div>
            <div>
              <strong>{step.number}. {step.title}</strong>
              <span>{step.body}</span>
            </div>
            <span className="step-action">{step.done ? "Done" : step.action}</span>
          </NavLink>
        ))}
      </div>
    </section>
  );
}

function OnboardingComplete() {
  return (
    <section className="panel onboarding-panel complete">
      <div className="summary-row">
        <ShieldCheck size={20} />
        <div>
          <strong>Onboarding complete</strong>
          <span>ClickHouse clusters, backup storage, policy, schedule, first backup, and restore practice are all present.</span>
        </div>
      </div>
    </section>
  );
}

function Backups() {
  const { api, showToast } = useApi();
  const [selected, setSelected] = useState<BackupDto | null>(null);
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const mutation = useMutation({
    mutationFn: ({ id, action }: { id: string; action: "pin" | "unpin" | "delete" }) =>
      action === "pin" ? api.pinBackup(id) : action === "unpin" ? api.unpinBackup(id) : api.deleteBackup(id),
    onSuccess: () => {
      showToast({ kind: "success", text: "Backup updated." });
      backups.refetch();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Backups" action={<ManualBackupButton />}>
      <section className="panel">
        <DataTable headers={["Status", "Type", "Created", "Policy", "Tables", "Pinned", "Actions"]}>
          {(backups.data ?? []).map((backup) => (
            <tr key={backup.id}>
              <td><Status value={backup.status} /></td>
              <td>{backup.backupType}</td>
              <td>{formatTime(backup.createdAt)}</td>
              <td>{backup.policyId ?? "manual"}</td>
              <td>{backup.tables.length}</td>
              <td>{backup.isPinned ? "yes" : "no"}</td>
              <td className="actions">
                <button className="ghost" onClick={() => setSelected(backup)}>Details</button>
                <button className="ghost" onClick={() => mutation.mutate({ id: backup.id, action: backup.isPinned ? "unpin" : "pin" })}>{backup.isPinned ? "Unpin" : "Pin"}</button>
                <button className="danger" onClick={() => mutation.mutate({ id: backup.id, action: "delete" })}>Delete</button>
              </td>
            </tr>
          ))}
        </DataTable>
      </section>
      {selected && <BackupDrawer backup={selected} onClose={() => setSelected(null)} />}
    </Page>
  );
}

function BackupDrawer({ backup, onClose }: { backup: BackupDto; onClose: () => void }) {
  const navigate = useNavigate();
  return (
    <Drawer title="Backup detail" onClose={onClose}>
      <div className="detail-list">
        <Detail label="Backup id" value={backup.id} />
        <Detail label="Status" value={backup.status} />
        <Detail label="Failure" value={backup.failureReason ?? backup.error ?? "none"} />
      </div>
      <h3>Tables and shards</h3>
      <DataTable headers={["Table", "Engine", "Status", "Shards", "S3 path"]}>
        {backup.tables.map((table) => (
          <tr key={table.id}>
            <td>{table.database}.{table.table}</td>
            <td>{table.engine}</td>
            <td><Status value={table.status} /></td>
            <td>{table.shards.length}</td>
            <td className="mono">{table.s3Path}</td>
          </tr>
        ))}
      </DataTable>
      <button className="primary" onClick={() => navigate("/restores", { state: { backupId: backup.id } })}><RotateCcw size={16} /> Start restore</button>
    </Drawer>
  );
}

function Restores() {
  const { api, showToast } = useApi();
  const restores = useQuery({ queryKey: ["restores"], queryFn: () => api.restores() });
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const [request, setRequest] = useState<InitiateRestoreRequest>({ backupId: "", targetClusterId: "", append: false, allowSchemaMismatch: false, layout: "Preserve" });
  const mutation = useMutation({
    mutationFn: () => api.initiateRestore(request),
    onSuccess: () => {
      showToast({ kind: "success", text: "Restore queued." });
      restores.refetch();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Restores">
      <section className="panel form-panel">
        <h2>Start restore</h2>
        <div className="form-grid">
          <Select label="Backup" value={request.backupId} onChange={(value) => setRequest({ ...request, backupId: value })} options={(backups.data ?? []).map((b) => [b.id, `${b.backupType} · ${formatTime(b.createdAt)}`])} />
          <Select label="Target cluster" value={request.targetClusterId} onChange={(value) => setRequest({ ...request, targetClusterId: value })} options={(clusters.data ?? []).map((c) => [c.id, c.name])} />
          <Input label="Target database" value={request.targetDatabase ?? ""} onChange={(value) => setRequest({ ...request, targetDatabase: value || null })} />
          <Input label="Target table" value={request.targetTable ?? ""} onChange={(value) => setRequest({ ...request, targetTable: value || null })} />
          <Select label="Layout" value={request.layout ?? "Preserve"} onChange={(value) => setRequest({ ...request, layout: value as RestoreLayout })} options={[["Preserve", "Preserve"], ["Redistribute", "Redistribute"], ["SingleNode", "Single node"]]} />
          <Input label="Source shard" type="number" value={request.sourceShard?.toString() ?? ""} onChange={(value) => setRequest({ ...request, sourceShard: value ? Number(value) : null })} />
          <label className="checkbox-row"><input type="checkbox" checked={request.append} onChange={(event) => setRequest({ ...request, append: event.target.checked })} /> Append into existing table</label>
          <label className="checkbox-row"><input type="checkbox" checked={request.allowSchemaMismatch} onChange={(event) => setRequest({ ...request, allowSchemaMismatch: event.target.checked })} /> Allow schema mismatch</label>
        </div>
        <button className="primary" onClick={() => mutation.mutate()}><RotateCcw size={16} /> Queue restore</button>
      </section>
      <section className="panel">
        <DataTable headers={["Status", "Backup", "Target", "Layout", "Started", "Failure"]}>
          {(restores.data ?? []).map((restore) => (
            <tr key={restore.id}>
              <td><Status value={restore.status} /></td>
              <td>{restore.backupId}</td>
              <td>{restore.targetClusterId}</td>
              <td>{restore.layout}</td>
              <td>{formatTime(restore.startedAt)}</td>
              <td>{restore.failureReason ?? ""}</td>
            </tr>
          ))}
        </DataTable>
      </section>
    </Page>
  );
}

function Policies() {
  const { api, showToast } = useApi();
  const navigate = useNavigate();
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<BackupPolicyDto | null>(null);
  const [createdPolicy, setCreatedPolicy] = useState<BackupPolicyDto | null>(null);
  const defaultPolicyDraft = (): UpsertPolicyRequest => ({ name: "", sourceClusterId: "", targetId: "", selector: emptySelector, retention: { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }, failedBackupRetentionMode: "KeepAndExcludeFromMinBackupsToKeep" });
  const [draft, setDraft] = useState<UpsertPolicyRequest>(() => defaultPolicyDraft());
  const policyErrors = validatePolicyDraft(draft);
  const simulation = useQuery({
    queryKey: ["policy-simulation", draft.sourceClusterId, draft.selector],
    queryFn: () => api.simulatePolicy({ sourceClusterId: draft.sourceClusterId, selector: draft.selector }),
    enabled: showForm && draft.sourceClusterId.length > 0,
    retry: false
  });
  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setDraft(defaultPolicyDraft());
  };
  const save = useMutation({
    mutationFn: () => editing ? api.updatePolicy(editing.id, draft) : api.addPolicy(draft),
    onSuccess: (policy) => {
      showToast({ kind: "success", text: "Policy saved." });
      policies.refetch();
      setCreatedPolicy(editing ? null : policy);
      reset();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const executePolicy = useMutation({
    mutationFn: (policy: BackupPolicyDto) => api.manualBackup({
      clusterId: policy.sourceClusterId,
      targetId: policy.targetId,
      selector: policy.selector,
      backupType: "Full",
      policyId: policy.id
    }),
    onSuccess: () => {
      showToast({ kind: "success", text: "Backup queued." });
      setCreatedPolicy(null);
      navigate("/backups");
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Policies" action={<button className="primary" onClick={() => {
      setEditing(null);
      setCreatedPolicy(null);
      setDraft({ ...defaultPolicyDraft(), sourceClusterId: clusters.data?.[0]?.id ?? "", targetId: targets.data?.[0]?.id ?? "" });
      setShowForm(true);
    }}><Save size={16} /> Add policy</button>}>
      <section className="panel">
        <DataTable headers={["Name", "Source", "Backup Storage", "Rules", "Retention", "Actions"]}>
          {(policies.data ?? []).map((policy) => (
            <tr key={policy.id}>
              <td>{policy.name}</td>
              <td>{nameOf(clusters.data, policy.sourceClusterId)}</td>
              <td>{nameOf(targets.data, policy.targetId)}</td>
              <td>{policy.selector.rules.length}</td>
              <td>{policy.retention ? `${policy.retention.minBackupsToKeep} backups` : "default"}</td>
              <td className="actions"><button className="ghost" onClick={() => {
                setEditing(policy);
                setCreatedPolicy(null);
                setDraft({ name: policy.name, sourceClusterId: policy.sourceClusterId, targetId: policy.targetId, selector: policy.selector, retention: policy.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }, failedBackupRetentionMode: policy.failedBackupRetentionMode });
                setShowForm(true);
              }}>Edit</button><button className="ghost" disabled={executePolicy.isPending} onClick={() => executePolicy.mutate(policy)}><Play size={14} /> Execute now</button></td>
            </tr>
          ))}
        </DataTable>
      </section>
      {createdPolicy && <section className="panel followup-panel">
        <div className="section-head">
          <div>
            <h2>Policy created</h2>
            <p>Do you want to schedule this policy or run the first backup now?</p>
          </div>
          <button className="ghost" onClick={() => setCreatedPolicy(null)}>Dismiss</button>
        </div>
        <div className="actions">
          <button className="primary" onClick={() => navigate("/schedules", { state: { policyId: createdPolicy.id } })}><CalendarClock size={16} /> Add schedule</button>
          <button className="secondary" disabled={executePolicy.isPending} onClick={() => executePolicy.mutate(createdPolicy)}><Play size={16} /> Execute backup now</button>
        </div>
      </section>}
      {showForm && <section className="panel form-panel">
        <div className="section-head"><h2>{editing ? "Edit policy" : "Create policy"}</h2><button className="ghost" onClick={reset}>Cancel</button></div>
        <div className="form-grid">
          <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
          <Select label="Source cluster" value={draft.sourceClusterId} onChange={(value) => setDraft({ ...draft, sourceClusterId: value })} options={(clusters.data ?? []).map((cluster) => [cluster.id, cluster.name])} />
          <Select label="Backup storage" value={draft.targetId} onChange={(value) => setDraft({ ...draft, targetId: value })} options={(targets.data ?? []).map((target) => [target.id, target.name])} />
          <Select label="Failed backups" value={draft.failedBackupRetentionMode} onChange={(value) => setDraft({ ...draft, failedBackupRetentionMode: value as FailedBackupRetentionMode })} options={[["KeepAndExcludeFromMinBackupsToKeep", "Keep"], ["DeleteByGarbageCollectorAfterFailure", "Garbage collect failed backups"]]} />
        </div>
        <SelectorBuilder selector={draft.selector} hasSource={draft.sourceClusterId.length > 0} inventory={simulation.data?.inventory ?? []} selected={simulation.data?.tables ?? []} isLoading={simulation.isFetching} error={simulation.error ? String(simulation.error) : null} onChange={(selector) => setDraft({ ...draft, selector })} />
        <RetentionEditor value={draft.retention ?? { fullRetentionMinutes: null, incrementalRetentionMinutes: null, minBackupsToKeep: 0, minFullBackupsToKeep: 0 }} onChange={(retention) => setDraft({ ...draft, retention })} />
        {policyErrors.map((error) => <span className="field-error" key={error}>{error}</span>)}
        <button className="primary" disabled={policyErrors.length > 0 || save.isPending} onClick={() => {
          if (policyErrors.length === 0) save.mutate();
        }}><Save size={16} /> Save policy</button>
      </section>}
    </Page>
  );
}

function SelectorBuilder({ selector, hasSource, inventory, selected, isLoading, error, onChange }: { selector: PolicySelector; hasSource: boolean; inventory: Array<{ database: string; table: string }>; selected: Array<{ database: string; table: string }>; isLoading: boolean; error: string | null; onChange: (selector: PolicySelector) => void }) {
  const updateRule = (index: number, rule: PolicySelectorRule) => onChange({ ...selector, rules: selector.rules.map((item, i) => i === index ? rule : item) });
  return (
    <div className="selector-builder">
      <div className="section-head">
        <h3>Selector rules</h3>
        <button className="secondary" onClick={() => onChange({ ...selector, rules: [...selector.rules, { action: "Exclude", database: { kind: "Exact", value: "" }, table: { kind: "All", value: "*" } }] })}><ListFilter size={16} /> Add rule</button>
      </div>
      {selector.rules.map((rule, index) => (
        <div className="rule-row" key={index}>
          <span className="drag-handle">{index + 1}</span>
          <select value={rule.action} onChange={(event) => updateRule(index, { ...rule, action: event.target.value as PolicySelectorAction })}>
            <option value="Include">Include</option>
            <option value="Exclude">Exclude</option>
          </select>
          <PatternEditor label="Database" pattern={rule.database} onChange={(database) => updateRule(index, { ...rule, database })} />
          <PatternEditor label="Table" pattern={rule.table} onChange={(table) => updateRule(index, { ...rule, table })} />
          <button className="ghost" onClick={() => index > 0 && onChange({ ...selector, rules: move(selector.rules, index, index - 1) })}>Up</button>
          <button className="ghost" onClick={() => onChange({ ...selector, rules: selector.rules.filter((_, i) => i !== index) })}>Remove</button>
        </div>
      ))}
      <div className="preview-grid">
        <pre>{JSON.stringify(selector, null, 2)}</pre>
        <div>
          {!hasSource && <span className="hint">Choose a source cluster to preview the selected tables.</span>}
          {hasSource && isLoading && <span className="hint">Loading tables from ClickHouse...</span>}
          {hasSource && !isLoading && error && <span className="field-error">{error}</span>}
          {hasSource && !isLoading && !error && inventory.length === 0 && <span className="hint">No tables were returned by ClickHouse for this cluster.</span>}
          {hasSource && !isLoading && !error && inventory.length > 0 && <>
            <h4>Tables selected</h4>
            <span className="hint">{selected.length} of {inventory.length} table(s) will be backed up.</span>
            {selected.map((table) => <span className="chip" key={`${table.database}.${table.table}`}>{table.database}.{table.table}</span>)}
          </>}
        </div>
      </div>
    </div>
  );
}

function PatternEditor({ label, pattern, onChange }: { label: string; pattern: { kind: PolicyMatchKind; value: string }; onChange: (pattern: { kind: PolicyMatchKind; value: string }) => void }) {
  return (
    <div className="pattern-editor">
      <span>{label}</span>
      <select value={pattern.kind} onChange={(event) => onChange({ kind: event.target.value as PolicyMatchKind, value: event.target.value === "All" ? "*" : pattern.value })}>
        <option value="All">all</option>
        <option value="Exact">exact</option>
        <option value="Wildcard">wildcard</option>
      </select>
      <input value={pattern.value} disabled={pattern.kind === "All"} onChange={(event) => onChange({ ...pattern, value: event.target.value })} />
    </div>
  );
}

function RetentionEditor({ value, onChange }: { value: NonNullable<UpsertPolicyRequest["retention"]>; onChange: (value: NonNullable<UpsertPolicyRequest["retention"]>) => void }) {
  const retention = value;
  return (
    <div className="retention-editor">
      <h3>Retention</h3>
      <span className="hint">Leave a retention minutes field empty for no time-based retention limit. Minimum counts of 0 mean no minimum backup count is protected.</span>
      <div className="form-grid">
        <Input label="Full retention minutes (empty = no retention)" type="number" value={retention.fullRetentionMinutes?.toString() ?? ""} onChange={(value) => onChange({ ...retention, fullRetentionMinutes: value ? Number(value) : null })} />
        <Input label="Incremental retention minutes (empty = no retention)" type="number" value={retention.incrementalRetentionMinutes?.toString() ?? ""} onChange={(value) => onChange({ ...retention, incrementalRetentionMinutes: value ? Number(value) : null })} />
        <Input label="Min backups to keep" type="number" value={`${retention.minBackupsToKeep}`} onChange={(value) => onChange({ ...retention, minBackupsToKeep: Number(value) || 0 })} />
        <Input label="Min full backups" type="number" value={`${retention.minFullBackupsToKeep}`} onChange={(value) => onChange({ ...retention, minFullBackupsToKeep: Number(value) || 0 })} />
      </div>
    </div>
  );
}

function Schedules() {
  const { api, showToast } = useApi();
  const location = useLocation();
  const schedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<BackupScheduleDto | null>(null);
  const [scheduleDraft, setScheduleDraft] = useState<ScheduleDraft>(defaultScheduleDraft);
  const [draft, setDraft] = useState<UpsertScheduleRequest>({ name: "", policyId: "", backupType: "Full", cronExpression: buildCron(defaultScheduleDraft), timeZoneId: "UTC", isEnabled: true, missedRunGracePeriod: null, description: null });
  useEffect(() => {
    const state = location.state as { policyId?: string } | null;
    if (!state?.policyId) return;
    setShowForm(true);
    setDraft((current) => ({ ...current, policyId: state.policyId ?? current.policyId }));
  }, [location.state]);
  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setScheduleDraft(defaultScheduleDraft);
    setDraft({ name: "", policyId: "", backupType: "Full", cronExpression: buildCron(defaultScheduleDraft), timeZoneId: "UTC", isEnabled: true, missedRunGracePeriod: null, description: null });
  };
  const cronExpression = scheduleDraft.cronExpression.trim();
  const cronValidation = useQuery({
    queryKey: ["schedule-cron-validation", cronExpression, draft.timeZoneId],
    queryFn: () => api.validateScheduleCron({ cronExpression, timeZoneId: draft.timeZoneId }),
    enabled: cronExpression.length > 0 && draft.timeZoneId.trim().length > 0,
    retry: false
  });
  const save = useMutation({
    mutationFn: () => {
      const request = { ...draft, cronExpression };
      return editing ? api.updateSchedule(editing.id, request) : api.addSchedule(request);
    },
    onSuccess: () => {
      showToast({ kind: "success", text: "Schedule saved." });
      schedules.refetch();
      reset();
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  const canSaveSchedule =
    draft.name.trim().length > 0 &&
    draft.policyId.trim().length > 0 &&
    cronValidation.data?.isValid === true &&
    !cronValidation.isFetching &&
    !save.isPending;
  return (
    <Page title="Schedules" action={<button className="primary" onClick={() => { reset(); setShowForm(true); }}><Save size={16} /> Add schedule</button>}>
      <section className="panel">
        <DataTable headers={["Name", "Policy", "Type", "Cron", "Timezone", "Enabled", "Actions"]}>
          {(schedules.data ?? []).map((schedule) => (
            <tr key={schedule.id}>
              <td>{schedule.name}</td>
              <td>{nameOf(policies.data, schedule.policyId)}</td>
              <td>{schedule.backupType}</td>
              <td className="mono">{schedule.cronExpression}</td>
              <td>{schedule.timeZoneId}</td>
              <td>{schedule.isEnabled ? "yes" : "no"}</td>
              <td><button className="ghost" onClick={() => {
                setEditing(schedule);
                setScheduleDraft(parseCronToDraft(schedule.cronExpression));
                setDraft({ name: schedule.name, policyId: schedule.policyId, backupType: schedule.backupType, cronExpression: schedule.cronExpression, timeZoneId: schedule.timeZoneId, isEnabled: schedule.isEnabled, missedRunGracePeriod: schedule.missedRunGracePeriod, description: schedule.description });
                setShowForm(true);
              }}>Edit</button></td>
            </tr>
          ))}
        </DataTable>
      </section>
      {showForm && <section className="panel form-panel">
        <div className="section-head"><h2>{editing ? "Edit schedule" : "Create schedule"}</h2><button className="ghost" onClick={reset}>Cancel</button></div>
        <div className="form-grid">
          <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
          <Select label="Policy" value={draft.policyId} onChange={(value) => setDraft({ ...draft, policyId: value })} options={(policies.data ?? []).map((policy) => [policy.id, policy.name])} />
          <Select label="Backup type" value={draft.backupType} onChange={(value) => setDraft({ ...draft, backupType: value as BackupType })} options={[["Full", "Full"], ["Incremental", "Incremental"]]} />
          <Input label="Timezone" value={draft.timeZoneId} onChange={(value) => setDraft({ ...draft, timeZoneId: value || "UTC" })} />
        </div>
        <ScheduleEditor draft={scheduleDraft} timezone={draft.timeZoneId} onChange={setScheduleDraft} />
        <div className={`summary-row ${cronValidation.data?.isValid ? "valid" : cronValidation.data && !cronValidation.data.isValid ? "invalid" : ""}`}>
          <CalendarClock size={18} />
          <div>
            <strong>{summarizeSchedule(scheduleDraft, draft.timeZoneId)}</strong>
            <span className="mono">{cronExpression || "(empty cron expression)"}</span>
            {cronValidation.isFetching && <span>Validating cron expression...</span>}
            {cronValidation.data?.isValid && <span>Next runs: {cronValidation.data.nextRuns.map(formatTime).join(", ") || "none in the next 30 days"}</span>}
            {cronValidation.data && !cronValidation.data.isValid && <span className="field-error">{cronValidation.data.error ?? "Cron expression is invalid."}</span>}
          </div>
        </div>
        <label className="checkbox-row"><input type="checkbox" checked={draft.isEnabled} onChange={(event) => setDraft({ ...draft, isEnabled: event.target.checked })} /> Enabled</label>
        <button className="primary" disabled={!canSaveSchedule} onClick={() => save.mutate()}><Save size={16} /> {cronValidation.data?.isValid ? "Save schedule" : "Validate cron before saving"}</button>
      </section>}
    </Page>
  );
}

function ScheduleEditor({ draft, timezone, onChange }: { draft: ScheduleDraft; timezone: string; onChange: (draft: ScheduleDraft) => void }) {
  const updateFromPreset = (next: ScheduleDraft) => onChange({ ...next, cronExpression: buildCron(next) });
  return (
    <div className="schedule-editor">
      <div className="preset-row">
        {(["daily", "weekly", "selected-days", "every-hours", "monthly", "advanced"] as const).map((preset) => (
          <button key={preset} className={draft.preset === preset ? "selected" : "ghost"} onClick={() => updateFromPreset({ ...draft, preset })}>{presetLabel(preset)}</button>
        ))}
      </div>
      {draft.preset !== "advanced" && (
        <div className="form-grid">
          {draft.preset !== "every-hours" && <Input label="Hour" type="number" value={`${draft.hour}`} onChange={(value) => updateFromPreset({ ...draft, hour: Number(value) })} />}
          <Input label="Minute" type="number" value={`${draft.minute}`} onChange={(value) => updateFromPreset({ ...draft, minute: Number(value) })} />
          {draft.preset === "weekly" && <Select label="Day" value={draft.dayOfWeek} onChange={(value) => updateFromPreset({ ...draft, dayOfWeek: value })} options={dayOptions.map((day) => [day.value, day.label])} />}
          {draft.preset === "selected-days" && <div className="day-picker">{dayOptions.map((day) => <label key={day.value}><input type="checkbox" checked={draft.selectedDays.includes(day.value)} onChange={(event) => updateFromPreset({ ...draft, selectedDays: event.target.checked ? [...draft.selectedDays, day.value] : draft.selectedDays.filter((x) => x !== day.value) })} /> {day.label.slice(0, 3)}</label>)}</div>}
          {draft.preset === "every-hours" && <Input label="Every N hours" type="number" value={`${draft.everyHours}`} onChange={(value) => updateFromPreset({ ...draft, everyHours: Number(value) })} />}
          {draft.preset === "monthly" && <Input label="Day of month" type="number" value={`${draft.dayOfMonth}`} onChange={(value) => updateFromPreset({ ...draft, dayOfMonth: Number(value) })} />}
        </div>
      )}
      <Input label="Quartz cron expression" value={draft.cronExpression} onChange={(value) => onChange({ ...draft, preset: "advanced", cronExpression: value })} />
      <span className="hint">{summarizeSchedule(draft, timezone)}</span>
    </div>
  );
}

function Clusters() {
  const { api, showToast } = useApi();
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const [showForm, setShowForm] = useState(false);
  const [modifyCredentials, setModifyCredentials] = useState(true);
  const [editing, setEditing] = useState<ClusterDto | null>(null);
  const [draft, setDraft] = useState<UpsertClusterRequest>({ name: "", mode: "SingleInstance", accessNodes: [{ host: "localhost", port: 9000, useTls: false }], userName: null, password: null });
  const clickHouseNames = useQuery({ queryKey: ["clickhouse-cluster-names", editing?.id], queryFn: () => api.clickHouseClusterNames(editing!.id), enabled: !!editing && draft.mode === "Cluster", retry: false });
  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setModifyCredentials(true);
    setDraft({ name: "", mode: "SingleInstance", accessNodes: [{ host: "localhost", port: 9000, useTls: false }], userName: null, password: null });
  };
  const save = useMutation({
    mutationFn: () => editing ? api.updateCluster(editing.id, modifyCredentials ? draft : { ...draft, userName: null, password: null }) : api.addCluster(draft),
    onSuccess: () => { showToast({ kind: "success", text: "Cluster saved." }); clusters.refetch(); reset(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <CrudPage title="ClickHouse Clusters" showForm={showForm} onAdd={() => { reset(); setShowForm(true); }} formTitle={editing ? "Edit ClickHouse cluster" : "Create ClickHouse cluster"} saveLabel={editing ? "Update cluster" : "Save cluster"} onCancel={reset} onSave={() => save.mutate()} form={<>
      <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
      <Select label="Mode" value={draft.mode} onChange={(value) => setDraft({ ...draft, mode: value as UpsertClusterRequest["mode"] })} options={[["SingleInstance", "Single instance"], ["Cluster", "Cluster"]]} />
      <Input label="Nodes" value={formatNodes(draft.accessNodes)} onChange={(value) => setDraft({ ...draft, accessNodes: parseNodes(value, draft.accessNodes.some((node) => node.useTls)) })} />
      <Input label="Max DOP" type="number" value={draft.backupRestoreMaxDop?.toString() ?? ""} onChange={(value) => setDraft({ ...draft, backupRestoreMaxDop: value ? Number(value) : null })} />
      {draft.mode === "Cluster" && (editing
        ? <Select label="ClickHouse system.clusters name" value={draft.clickHouseClusterName ?? ""} onChange={(value) => setDraft({ ...draft, clickHouseClusterName: value || null })} options={clickHouseClusterNameOptions(clickHouseNames.data?.names ?? [], draft.clickHouseClusterName)} />
        : <Input label="ClickHouse system.clusters name" value={draft.clickHouseClusterName ?? ""} onChange={(value) => setDraft({ ...draft, clickHouseClusterName: value || null })} />)}
      {draft.mode === "Cluster" && <span className="hint field-wide">{editing ? "Choose the ClickHouse topology entry Chobo should use. Leave empty when ClickHouse exposes exactly one cluster." : "After saving the connection, edit this cluster to choose from the live system.clusters list."}</span>}
      <label className="checkbox-row"><input type="checkbox" checked={draft.accessNodes.some((node) => node.useTls)} onChange={(event) => setDraft({ ...draft, accessNodes: draft.accessNodes.map((node) => ({ ...node, useTls: event.target.checked })) })} /> Use TLS</label>
      {editing && <label className="checkbox-row"><input type="checkbox" checked={modifyCredentials} onChange={(event) => setModifyCredentials(event.target.checked)} /> Modify credentials</label>}
      {(!editing || modifyCredentials) && <Input label="Username" value={draft.userName ?? ""} onChange={(value) => setDraft({ ...draft, userName: value || null })} />}
      {(!editing || modifyCredentials) && <Input label="Password" type="password" value={draft.password ?? ""} onChange={(value) => setDraft({ ...draft, password: value || null })} />}
    </>} table={<DataTable headers={["Name", "Mode", "Nodes", "Max DOP", "Actions"]}>{(clusters.data ?? []).map((cluster) => <tr key={cluster.id}><td>{cluster.name}</td><td>{cluster.mode}</td><td>{cluster.accessNodes.map((node) => `${node.host}:${node.port}`).join(", ")}</td><td>{cluster.backupRestoreMaxDop ?? "default"}</td><td className="actions"><button className="ghost" onClick={() => {
      setEditing(cluster);
      setShowForm(true);
      setModifyCredentials(false);
      setDraft({ name: cluster.name, mode: cluster.mode, accessNodes: cluster.accessNodes.map((node) => ({ host: node.host, port: node.port, useTls: node.useTls })), userName: null, password: null, backupRestoreMaxDop: cluster.backupRestoreMaxDop, clickHouseClusterName: cluster.clickHouseClusterName });
    }}>Edit</button><button className="ghost" onClick={() => api.testCluster(cluster.id).then((x) => showToast({ kind: x.succeeded ? "success" : "error", text: x.message }))}>Test</button></td></tr>)}</DataTable>} />
  );
}

function Targets() {
  const { api, showToast } = useApi();
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const [showForm, setShowForm] = useState(false);
  const [modifyCredentials, setModifyCredentials] = useState(true);
  const [editing, setEditing] = useState<BackupTargetDto | null>(null);
  const [draft, setDraft] = useState<UpsertS3TargetRequest>({ name: "", endpoint: "http://localhost:9000", region: "us-east-1", bucket: "", pathPrefix: null, forcePathStyle: true, accessKey: null, secretKey: null });
  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setModifyCredentials(true);
    setDraft({ name: "", endpoint: "http://localhost:9000", region: "us-east-1", bucket: "", pathPrefix: null, forcePathStyle: true, accessKey: null, secretKey: null });
  };
  const save = useMutation({
    mutationFn: () => editing ? api.updateTarget(editing.id, modifyCredentials ? draft : { ...draft, accessKey: null, secretKey: null }) : api.addTarget(draft),
    onSuccess: () => { showToast({ kind: "success", text: "Backup storage saved." }); targets.refetch(); reset(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return <CrudPage title="Backup Storage" showForm={showForm} onAdd={() => { reset(); setShowForm(true); }} formTitle={editing ? "Edit backup storage" : "Create backup storage"} saveLabel={editing ? "Update storage" : "Save storage"} onCancel={reset} onSave={() => save.mutate()} form={<>
    <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
    <Input label="Endpoint" value={draft.endpoint} onChange={(value) => setDraft({ ...draft, endpoint: value })} />
    <Input label="Bucket" value={draft.bucket} onChange={(value) => setDraft({ ...draft, bucket: value })} />
    <Input label="Region" value={draft.region} onChange={(value) => setDraft({ ...draft, region: value })} />
    <Input label="Path prefix" value={draft.pathPrefix ?? ""} onChange={(value) => setDraft({ ...draft, pathPrefix: value || null })} />
    <div className="field-wide">
      <span className="field-label">S3 addressing style</span>
      <div className="segmented">
        <button type="button" className={draft.forcePathStyle ? "selected" : "ghost"} onClick={() => setDraft({ ...draft, forcePathStyle: true })}>Path style</button>
        <button type="button" className={!draft.forcePathStyle ? "selected" : "ghost"} onClick={() => setDraft({ ...draft, forcePathStyle: false })}>Virtual-host style</button>
      </div>
      <span className="hint">
        {draft.forcePathStyle
          ? "Uses endpoint/bucket/key. Best for MinIO and many S3-compatible services."
          : "Uses bucket.endpoint/key. Use when your S3 provider requires virtual-hosted buckets."}
      </span>
    </div>
    {editing && <label className="checkbox-row"><input type="checkbox" checked={modifyCredentials} onChange={(event) => setModifyCredentials(event.target.checked)} /> Modify credentials</label>}
    {(!editing || modifyCredentials) && <Input label="Access key" value={draft.accessKey ?? ""} onChange={(value) => setDraft({ ...draft, accessKey: value || null })} />}
    {(!editing || modifyCredentials) && <Input label="Secret key" type="password" value={draft.secretKey ?? ""} onChange={(value) => setDraft({ ...draft, secretKey: value || null })} />}
  </>} table={<DataTable headers={["Name", "Endpoint", "Bucket", "Addressing", "Prefix", "Actions"]}>{(targets.data ?? []).map((target) => <tr key={target.id}><td>{target.name}</td><td>{target.s3.endpoint}</td><td>{target.s3.bucket}</td><td>{target.s3.forcePathStyle ? "Path style" : "Virtual-host style"}</td><td>{target.s3.pathPrefix ?? ""}</td><td className="actions"><button className="ghost" onClick={() => {
    setEditing(target);
    setShowForm(true);
    setModifyCredentials(false);
    setDraft({ name: target.name, endpoint: target.s3.endpoint, region: target.s3.region, bucket: target.s3.bucket, pathPrefix: target.s3.pathPrefix, forcePathStyle: target.s3.forcePathStyle, accessKey: null, secretKey: null });
  }}>Edit</button><button className="ghost" onClick={() => api.testTarget(target.id).then((x) => showToast({ kind: x.succeeded ? "success" : "error", text: x.message }))}>Test</button></td></tr>)}</DataTable>} />;
}

function UsersPage() {
  const { api, showToast } = useApi();
  const users = useQuery({ queryKey: ["users"], queryFn: () => api.users() });
  const [showForm, setShowForm] = useState(false);
  const [name, setName] = useState("");
  const [oneTimeToken, setOneTimeToken] = useState<CreateUserResponse | CreateAccessTokenResponse | null>(null);
  const create = useMutation({
    mutationFn: () => api.addUser({ userName: name }),
    onSuccess: (result) => { setOneTimeToken(result); setShowForm(false); setName(""); showToast({ kind: "success", text: "User created. Copy the token now." }); users.refetch(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Users" action={<button className="primary" onClick={() => setShowForm(true)}><Users size={16} /> Add user</button>}>
      <section className="panel">
        <DataTable headers={["User", "Active", "Created", "Actions"]}>{(users.data ?? []).map((user) => <UserRow key={user.id} user={user} />)}</DataTable>
      </section>
      {showForm && (
      <section className="panel form-panel">
        <div className="section-head"><h2>Create user</h2><button className="ghost" onClick={() => setShowForm(false)}>Cancel</button></div>
        <Input label="User name" value={name} onChange={setName} />
        <button className="primary" onClick={() => create.mutate()}><Users size={16} /> Add user</button>
      </section>)}
      {oneTimeToken && <section className="panel"><h2>One-time token</h2><pre className="token-box">{oneTimeToken.accessToken}</pre></section>}
    </Page>
  );
}

function UserRow({ user }: { user: UserDto }) {
  const { api, showToast } = useApi();
  const tokens = useQuery({ queryKey: ["tokens", user.id], queryFn: () => api.tokens(user.id), enabled: false });
  return <tr><td>{user.userName}</td><td>{user.isActive ? "yes" : "no"}</td><td>{formatTime(user.createdAt)}</td><td className="actions"><button className="ghost" onClick={() => tokens.refetch()}>Tokens</button>{tokens.data?.map((token: AccessTokenDto) => <span className="chip" key={token.id}>{token.name}</span>)}<button className="ghost" onClick={() => api.addToken(user.id, { name: "browser" }).then((result) => showToast({ kind: "success", text: `Token: ${result.accessToken}` }))}>New token</button></td></tr>;
}

function Logs() {
  const { api, showToast } = useApi();
  const [last, setLast] = useState(200);
  const logs = useQuery({ queryKey: ["logs", last], queryFn: () => api.logs({ last }) });
  return <EntriesPage title="Logs" last={last} setLast={setLast} onClear={(before) => api.clearLogs(before).then(() => showToast({ kind: "success", text: "Logs cleared." })).then(() => logs.refetch())} headers={["Time", "Level", "Category", "Message"]} rows={(logs.data ?? []).map((log) => [formatTime(log.timestamp), log.level, log.category, log.message])} />;
}

function Audit() {
  const { api, showToast } = useApi();
  const [last, setLast] = useState(200);
  const audits = useQuery({ queryKey: ["audit", last], queryFn: () => api.audits({ last }) });
  return <EntriesPage title="Audit" last={last} setLast={setLast} onClear={(before) => api.clearAudits(before).then(() => showToast({ kind: "success", text: "Audit cleared." })).then(() => audits.refetch())} headers={["Time", "Actor", "Action", "Entity", "Details"]} rows={(audits.data ?? []).map((audit: AuditEntryDto) => [formatTime(audit.timestamp), audit.actorName, audit.action, `${audit.entityType}:${audit.entityId ?? ""}`, JSON.stringify(audit.details)])} />;
}

function ImportExport() {
  const { api, showToast } = useApi();
  const [text, setText] = useState("");
  const download = async (kind: "data" | "config") => {
    const envelope = kind === "data" ? await api.exportData() : await api.exportConfig();
    const blob = new Blob([JSON.stringify(envelope, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `chobo-${kind}.json`;
    link.click();
    URL.revokeObjectURL(url);
  };
  const upload = async (kind: "data" | "config") => {
    const envelope = JSON.parse(text);
    if (kind === "data") await api.importData(envelope);
    else await api.importConfig(envelope);
    showToast({ kind: "success", text: `${kind} imported.` });
  };
  return (
    <Page title="Import/Export">
      <section className="panel actions-panel">
        <button className="secondary" onClick={() => download("config")}><Download size={16} /> Export config</button>
        <button className="secondary" onClick={() => download("data")}><Download size={16} /> Export data</button>
      </section>
      <section className="panel">
        <h2>Import JSON</h2>
        <textarea value={text} onChange={(event) => setText(event.target.value)} placeholder="Paste exported Chobo JSON here" />
        <div className="actions"><button className="primary" onClick={() => upload("config")}><Upload size={16} /> Import config</button><button className="danger" onClick={() => upload("data")}><Upload size={16} /> Import data</button></div>
      </section>
    </Page>
  );
}

function ManualBackupButton() {
  const navigate = useNavigate();
  return <button className="primary" onClick={() => navigate("/policies")}><Play size={16} /> Manual backup</button>;
}

function EntriesPage({ title, last, setLast, onClear, headers, rows }: { title: string; last: number; setLast: (value: number) => void; onClear: (before: string) => void; headers: string[]; rows: string[][] }) {
  const [filters, setFilters] = useState<string[]>(() => headers.map(() => ""));
  const [activeFilter, setActiveFilter] = useState<number | null>(null);
  const visibleRows = rows.filter((row) => filters.every((filter, index) => !filter.trim() || row[index]?.toLowerCase().includes(filter.trim().toLowerCase())));
  return <Page title={title} action={<Input label="Last" type="number" value={`${last}`} onChange={(value) => setLast(Number(value) || 100)} />}>
    <section className="panel">
      <div className="table-wrap"><table><thead><tr>{headers.map((header, index) => (
        <th key={header}>
          <div className="filterable-header">
            <span>{header}</span>
            <button className={`header-filter ${filters[index] ? "active" : ""}`} title={`Filter ${header}`} onClick={() => setActiveFilter(activeFilter === index ? null : index)}><ListFilter size={13} /></button>
            {activeFilter === index && <div className="filter-popover">
              <label>{header} contains<input autoFocus value={filters[index] ?? ""} onChange={(event) => setFilters(filters.map((filter, i) => i === index ? event.target.value : filter))} onKeyDown={(event) => {
                if (event.key === "Escape") setActiveFilter(null);
              }} /></label>
              <div className="actions">
                <button className="ghost" onClick={() => setFilters(filters.map((filter, i) => i === index ? "" : filter))}>Clear</button>
                <button className="secondary" onClick={() => setActiveFilter(null)}>Apply</button>
              </div>
            </div>}
          </div>
        </th>
      ))}</tr></thead><tbody>{visibleRows.map((row, index) => <tr key={index}>{row.map((cell, i) => <td key={i}>{cell}</td>)}</tr>)}</tbody></table></div>
      <span className="hint">{visibleRows.length} of {rows.length} row(s)</span>
      <button className="danger" onClick={() => onClear(new Date().toISOString())}>Clear before now</button>
    </section>
  </Page>;
}

function CrudPage({ title, showForm, onAdd, formTitle, saveLabel = "Save", form, table, onSave, onCancel }: { title: string; showForm: boolean; onAdd: () => void; formTitle?: string; saveLabel?: string; form: ReactNode; table: ReactNode; onSave: () => void; onCancel?: () => void }) {
  return <Page title={title} action={!showForm ? <button className="primary" onClick={onAdd}><Save size={16} /> Add</button> : undefined}><section className="panel">{table}</section>{showForm && <section className="panel form-panel"><div className="section-head">{formTitle && <h2>{formTitle}</h2>}{onCancel && <button className="ghost" onClick={onCancel}>Cancel</button>}</div><div className="form-grid">{form}</div><div className="actions"><button className="primary" onClick={onSave}><Save size={16} /> {saveLabel}</button></div></section>}</Page>;
}

function Page({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return <div className="page"><div className="page-head"><div><h1>{title}</h1><p>Manage Chobo operations and configuration.</p></div>{action}</div>{children}</div>;
}

function DataTable({ headers, children }: { headers: string[]; children: ReactNode }) {
  return <div className="table-wrap"><table><thead><tr>{headers.map((header) => <th key={header}>{header}</th>)}</tr></thead><tbody>{children}</tbody></table></div>;
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: "ok" | "warn" | "bad" }) {
  return <div className={`stat ${tone ?? ""}`}><span>{label}</span><strong>{value}</strong></div>;
}

function Status({ value }: { value: string }) {
  const tone = /failed|delete|error/i.test(value) ? "bad" : /running|queued|partial/i.test(value) ? "warn" : /succeed|enabled|ok/i.test(value) ? "ok" : "";
  return <span className={`status ${tone}`}>{value}</span>;
}

function Drawer({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return <div className="drawer-backdrop" onClick={onClose}><aside className="drawer" onClick={(event) => event.stopPropagation()}><div className="section-head"><h2>{title}</h2><button className="ghost" onClick={onClose}>Close</button></div>{children}</aside></div>;
}

function Empty({ text }: { text: string }) {
  return <div className="empty">{text}</div>;
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div><span>{label}</span><strong>{value}</strong></div>;
}

function Input({ label, value, onChange, type = "text" }: { label: string; value: string; onChange: (value: string) => void; type?: string }) {
  return <label>{label}<input type={type} value={value} onChange={(event) => onChange(event.target.value)} /></label>;
}

function Select({ label, value, onChange, options }: { label: string; value: string; onChange: (value: string) => void; options: string[][] }) {
  return <label>{label}<select value={value} onChange={(event) => onChange(event.target.value)}><option value="">Select...</option>{options.map(([optionValue, optionLabel]) => <option key={optionValue} value={optionValue}>{optionLabel}</option>)}</select></label>;
}

function move<T>(items: T[], from: number, to: number) {
  const copy = [...items];
  const [item] = copy.splice(from, 1);
  copy.splice(to, 0, item);
  return copy;
}

function nameOf(items: Array<{ id: string; name: string }> | undefined, id: string) {
  return items?.find((item) => item.id === id)?.name ?? id;
}

function validatePolicyDraft(draft: UpsertPolicyRequest) {
  const errors: string[] = [];
  if (!draft.name.trim()) errors.push("Policy name is required.");
  if (!draft.sourceClusterId) errors.push("Choose a source cluster.");
  if (!draft.targetId) errors.push("Choose backup storage.");
  if (draft.selector.rules.length === 0) errors.push("Add at least one selector rule.");
  return errors;
}

function formatNodes(nodes: UpsertClusterRequest["accessNodes"]) {
  return nodes.map((node) => `${node.host}:${node.port}`).join(", ");
}

function parseNodes(value: string, useTls: boolean): UpsertClusterRequest["accessNodes"] {
  const nodes = value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .map((item) => {
      const [host, port] = item.split(":");
      return { host, port: Number(port) || 9000, useTls };
    });

  return nodes.length > 0 ? nodes : [{ host: "", port: 9000, useTls }];
}

function clickHouseClusterNameOptions(names: string[], current?: string | null) {
  const options = [...names];
  if (current && !options.includes(current)) options.unshift(current);
  return options.map((name) => [name, name]);
}

function presetLabel(value: string) {
  return value.split("-").map((part) => part[0].toUpperCase() + part.slice(1)).join(" ");
}

function formatTime(value?: string | null) {
  if (!value) return "never";
  return new Date(value).toLocaleString();
}
