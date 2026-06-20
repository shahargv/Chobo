import { NavLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Activity, CheckCircle2, Circle, ShieldCheck } from "lucide-react";
import { useApi } from "../api-context";
import { DataTable, Empty, Page, Stat, Status } from "../components/ui";
import { formatTime } from "../utils/format";

export function Dashboard() {
  const { api } = useApi();
  const dashboard = useQuery({ queryKey: ["dashboard"], queryFn: () => api.dashboard(12) });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const allSchedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const restores = useQuery({ queryKey: ["restores"], queryFn: () => api.restores() });
  const running = dashboard.data?.runningBackups ?? [];
  const schedules = dashboard.data?.schedules ?? [];
  const failures = schedules.filter((schedule) => schedule.lastRunFailureReason);
  const latestBackups = [...(backups.data ?? [])]
    .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())
    .slice(0, 8);
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
    <Page title="Dashboard" action={<button className="secondary" onClick={() => { dashboard.refetch(); backups.refetch(); }}><Activity size={16} /> Refresh</button>}>
      {missingOnboarding.length > 0 ? <OnboardingPanel steps={onboarding} /> : <OnboardingComplete />}
      <div className="stat-grid">
        <Stat label="Running backups" value={running.length} tone={running.length ? "warn" : "ok"} />
        <Stat label="Enabled schedules" value={schedules.filter((x) => x.isEnabled).length} />
        <Stat label="Recent failures" value={failures.length} tone={failures.length ? "bad" : "ok"} />
        <Stat label="Latest backups" value={latestBackups.length} />
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
          <h2>Latest backups</h2>
          <DataTable headers={["Status", "Created", "Type", "Tables", "Failure"]}>
            {latestBackups.map((backup) => (
              <tr key={backup.id}>
                <td><Status value={backup.status} /></td>
                <td>{formatTime(backup.createdAt)}</td>
                <td>{backup.backupType}</td>
                <td>{backup.tables.length}</td>
                <td>{backup.failureReason ?? backup.error ?? ""}</td>
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
