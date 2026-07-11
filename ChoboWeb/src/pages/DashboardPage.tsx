import { NavLink } from "react-router-dom";
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CalendarX, CheckCircle2, Circle, Info, ShieldCheck } from "lucide-react";
import type { BackupDto } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Empty, ExpandableErrorText, Page, Stat, Status } from "../components/ui";
import { BackupDrawer } from "./BackupsPage";
import { formatCompletionTime, formatDurationSeconds, formatTime } from "../utils/format";

export function Dashboard() {
  const { api } = useApi();
  const [futureWindowHours, setFutureWindowHours] = useState(6);
  const [selectedBackupId, setSelectedBackupId] = useState<string | null>(null);
  const dashboard = useQuery({ queryKey: ["dashboard", futureWindowHours], queryFn: () => api.dashboard(futureWindowHours) });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const allSchedules = useQuery({ queryKey: ["schedules"], queryFn: () => api.schedules() });
  const backups = useQuery({ queryKey: ["backups", "summary"], queryFn: () => api.backups({}, { includeTables: false }) });
  const missingBackups = useQuery({ queryKey: ["dashboard", "missing-backups", 24], queryFn: () => api.missingBackups(24) });
  const restores = useQuery({ queryKey: ["restores"], queryFn: () => api.restores() });
  const running = dashboard.data?.runningBackups ?? [];
  const schedules = dashboard.data?.schedules ?? [];
  const failures = schedules
    .filter((schedule) => schedule.lastRunFailureReason)
    .sort((left, right) => new Date(right.lastRunAt ?? 0).getTime() - new Date(left.lastRunAt ?? 0).getTime());
  const missing = missingBackups.data ?? [];
  const sortedBackups = [...(backups.data ?? [])]
    .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime());
  const latestBackups = sortedBackups.slice(0, 8);
  const latestBackupByScheduleId = latestBackupBySchedule(sortedBackups);
  const onboardingIsLoading = [clusters, targets, policies, allSchedules, backups, restores].some((query) => query.isLoading);
  const onboarding = onboardingIsLoading ? [] : buildOnboardingSteps({
    clusterCount: clusters.data?.filter((x) => !x.isDeleted).length ?? 0,
    targetCount: targets.data?.filter((x) => !x.isDeleted).length ?? 0,
    policyCount: policies.data?.filter((x) => !x.isDeleted && !x.isSystemDefault).length ?? 0,
    scheduleCount: allSchedules.data?.filter((x) => !x.isDeleted && !x.isSystemDefault).length ?? 0,
    backupCount: backups.data?.length ?? 0,
    restoreCount: restores.data?.length ?? 0
  });
  const missingOnboarding = onboarding.filter((step) => !step.done);
  return (
    <Page title="Dashboard" subtitle="See upcoming schedules, running backups, and recent operational health at a glance.">
      {onboardingIsLoading ? <OnboardingLoading /> : missingOnboarding.length > 0 ? <OnboardingPanel steps={onboarding} /> : <OnboardingComplete />}
      <div className="stat-grid">
        <Stat label="Running backups" value={running.length} tone={running.length ? "warn" : "ok"} />
        <Stat label="Enabled schedules" value={schedules.filter((x) => x.isEnabled).length} />
        <Stat label="Recent failures" value={failures.length} tone={failures.length ? "bad" : "ok"} />
        <Stat label="Missed backups" value={missing.length} tone={missing.length ? "bad" : "ok"} />
      </div>
      <section className="panel">
        <div className="section-head">
          <div>
            <h2>Upcoming backups</h2>
            <p>Backup runs planned for the next {dashboard.data?.futureWindowHours ?? futureWindowHours} hour(s).</p>
          </div>
          <div className="segmented" aria-label="Upcoming backup window">
            {[6, 12, 24, 48].map((hours) => (
              <button key={hours} className={futureWindowHours === hours ? "selected" : "ghost"} onClick={() => setFutureWindowHours(hours)}>{hours}h</button>
            ))}
          </div>
        </div>
        <DataTable headers={["Planned run", "Policy", "Policy id", "Schedule", "Schedule id", "Type"]} isLoading={dashboard.isLoading}>
          {(dashboard.data?.futureSchedules ?? []).map((planned) => (
            <tr key={`${planned.scheduleId}-${planned.plannedRunAt}`}>
              <td>{formatTime(planned.plannedRunAt)}</td>
              <td><NavLink to={`/policies/${planned.policyId}`}>{planned.policyName ?? planned.policyId}</NavLink></td>
              <td className="mono">{planned.policyId}</td>
              <td><NavLink to={`/schedules/${planned.scheduleId}`}>{planned.scheduleName}</NavLink></td>
              <td className="mono">{planned.scheduleId}</td>
              <td>{planned.backupType}</td>
            </tr>
          ))}
        </DataTable>
      </section>
      <section className="panel">
        <h2>Running backups</h2>
        <DataTable headers={["Backup", "Status", "Policy", "Started", "Tables", "Table-shards", "Failure"]} isLoading={dashboard.isLoading}>
          {running.map((backup) => (
            <tr key={backup.backupId}>
              <td><button type="button" className="link-button mono" onClick={() => setSelectedBackupId(backup.backupId)}>{backup.backupId}</button></td>
              <td><Status value={backup.status} /></td>
              <td>{backup.policyName ?? backup.policyId ?? "manual"}</td>
              <td>{formatTime(backup.startedAt)}</td>
              <td>{backup.tableCount}</td>
              <td>{backup.succeededShardCount}/{backup.shardCount} ok · {backup.failedShardCount} failed · {backup.runningShardCount} running</td>
              <td>{backup.failureReason ? <ExpandableErrorText text={backup.failureReason} title={`Backup ${backup.backupId} failure`} /> : ""}</td>
            </tr>
          ))}
        </DataTable>
      </section>
      <section className="panel">
        <div className="section-head">
          <div>
            <h2>Missed backups</h2>
            <p>Scheduled runs missed in the last 24 hours.</p>
          </div>
        </div>
        <DataTable headers={["Planned run", "Detected", "Policy", "Schedule", "Lateness", "Grace"]} isLoading={missingBackups.isLoading}>
          {missing.map((missed) => (
            <tr key={missed.auditId}>
              <td>{formatTime(missed.plannedRunAt)}</td>
              <td>{formatTime(missed.detectedAt ?? missed.auditedAt)}</td>
              <td>{missed.policyId ? <NavLink to={`/policies/${missed.policyId}`}>{missed.policyName ?? missed.policyId}</NavLink> : "unknown"}</td>
              <td>{missed.scheduleId ? <NavLink to={`/schedules/${missed.scheduleId}`}>{missed.scheduleName ?? missed.scheduleId}</NavLink> : "unknown"}</td>
              <td><span className="restore-source-table-name"><CalendarX size={16} /> {formatDurationSeconds(missed.latenessSeconds)}</span></td>
              <td>{formatDurationSeconds(missed.gracePeriodSeconds)}</td>
            </tr>
          ))}
        </DataTable>
        {!missingBackups.isLoading && missing.length === 0 ? <Empty text="No missed scheduled backups in the last 24 hours." /> : null}
      </section>
      <section className="panel two-col">
        <div>
          <h2>Latest backups</h2>
          <DataTable headers={["Backup", "Status", "Created", "Completed", "Type", "Tables", "Failure"]} isLoading={backups.isLoading}>
            {latestBackups.map((backup) => (
              <tr key={backup.id}>
                <td><button type="button" className="link-button mono" onClick={() => setSelectedBackupId(backup.id)}>{backup.id}</button></td>
                <td><Status value={backup.status} /></td>
                <td>{formatTime(backup.createdAt)}</td>
                <td>{formatCompletionTime(backup.endedAt ?? backup.deletedAt, backup.startedAt, backup.createdAt)}</td>
                <td>{backup.backupType}</td>
                <td>{backup.tableCount}</td>
                <td>{backup.failureReason || backup.error ? <ExpandableErrorText text={backup.failureReason ?? backup.error} title={`Backup ${backup.id} failure`} /> : ""}</td>
              </tr>
            ))}
          </DataTable>
        </div>
        <div>
          <h2>Recent failures</h2>
          <div className="stack">
            {failures.length === 0 ? <Empty text="No schedule failures in the current dashboard window." /> : failures.map((failure) => {
              const backup = latestBackupByScheduleId.get(failure.scheduleId);
              return <div className="summary-row failure-summary-row" key={failure.scheduleId}>
                <Status value={failure.lastRunStatus ?? "Failed"} />
                <div><strong>{failure.scheduleName}</strong><span>Failed {formatTime(failure.lastRunAt)}</span></div>
                {backup && <button type="button" className="ghost icon-button" title="Open backup details" aria-label={`Open backup details for ${failure.scheduleName ?? failure.scheduleId}`} onClick={() => setSelectedBackupId(backup.id)}><Info size={16} /></button>}
              </div>;
            })}
          </div>
        </div>
      </section>
      {selectedBackupId && <BackupDrawer backupId={selectedBackupId} onClose={() => setSelectedBackupId(null)} onOpenBackup={setSelectedBackupId} />}
    </Page>
  );
}


function latestBackupBySchedule(backups: BackupDto[]) {
  const byScheduleId = new Map<string, BackupDto>();
  for (const backup of backups) {
    if (!backup.scheduleId || byScheduleId.has(backup.scheduleId)) continue;
    byScheduleId.set(backup.scheduleId, backup);
  }
  return byScheduleId;
}
function OnboardingLoading() {
  return (
    <section className="panel onboarding-panel">
      <div className="summary-row">
        <Circle size={20} />
        <div>
          <strong>Checking onboarding status</strong>
          <span>Loading setup progress...</span>
        </div>
      </div>
    </section>
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

