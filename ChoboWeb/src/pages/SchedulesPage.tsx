import { useEffect, useState } from "react";
import { useLocation } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CalendarClock, Save } from "lucide-react";
import type { BackupScheduleDto, BackupType, UpsertScheduleRequest } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Input, Page, Select } from "../components/ui";
import { buildCron, dayOptions, defaultScheduleDraft, parseCronToDraft, type ScheduleDraft, summarizeSchedule } from "../schedule";
import { formatTime, nameOf, presetLabel } from "../utils/format";
export function Schedules() {
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

