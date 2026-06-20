export type SchedulePreset = "daily" | "weekly" | "selected-days" | "every-hours" | "monthly" | "advanced";

export interface ScheduleDraft {
  preset: SchedulePreset;
  hour: number;
  minute: number;
  dayOfWeek: string;
  selectedDays: string[];
  everyHours: number;
  dayOfMonth: number;
  cronExpression: string;
}

export const dayOptions = [
  { value: "SUN", label: "Sunday" },
  { value: "MON", label: "Monday" },
  { value: "TUE", label: "Tuesday" },
  { value: "WED", label: "Wednesday" },
  { value: "THU", label: "Thursday" },
  { value: "FRI", label: "Friday" },
  { value: "SAT", label: "Saturday" }
];

export const defaultScheduleDraft: ScheduleDraft = {
  preset: "daily",
  hour: 3,
  minute: 0,
  dayOfWeek: "SUN",
  selectedDays: ["SUN", "THU"],
  everyHours: 6,
  dayOfMonth: 1,
  cronExpression: "0 0 3 * * ?"
};

export function buildCron(draft: ScheduleDraft) {
  const minute = clamp(draft.minute, 0, 59);
  const hour = clamp(draft.hour, 0, 23);
  switch (draft.preset) {
    case "daily":
      return `0 ${minute} ${hour} * * ?`;
    case "weekly":
      return `0 ${minute} ${hour} ? * ${draft.dayOfWeek}`;
    case "selected-days": {
      const days = draft.selectedDays.length > 0 ? draft.selectedDays.join(",") : draft.dayOfWeek;
      return `0 ${minute} ${hour} ? * ${days}`;
    }
    case "every-hours":
      return `0 ${minute} 0/${clamp(draft.everyHours, 1, 23)} * * ?`;
    case "monthly":
      return `0 ${minute} ${hour} ${clamp(draft.dayOfMonth, 1, 31)} * ?`;
    case "advanced":
      return draft.cronExpression.trim();
  }
}

export function summarizeSchedule(draft: ScheduleDraft, timezone: string) {
  const time = `${pad(draft.hour)}:${pad(draft.minute)}`;
  switch (draft.preset) {
    case "daily":
      return `Daily at ${time} (${timezone})`;
    case "weekly":
      return `Every ${labelForDay(draft.dayOfWeek)} at ${time} (${timezone})`;
    case "selected-days":
      return `Every ${draft.selectedDays.map(labelForDay).join(" and ")} at ${time} (${timezone})`;
    case "every-hours":
      return `Every ${draft.everyHours} hour${draft.everyHours === 1 ? "" : "s"} at minute ${pad(draft.minute)} (${timezone})`;
    case "monthly":
      return `Monthly on day ${draft.dayOfMonth} at ${time} (${timezone})`;
    case "advanced":
      return `Advanced cron: ${draft.cronExpression || "(empty)"}`;
  }
}

export function parseCronToDraft(cronExpression: string): ScheduleDraft {
  const parts = cronExpression.trim().split(/\s+/);
  if (parts.length < 6) return { ...defaultScheduleDraft, preset: "advanced", cronExpression };
  const [, minute, hour, dayOfMonth, , dayOfWeek] = parts;
  const parsedMinute = Number.parseInt(minute, 10);
  const parsedHour = Number.parseInt(hour, 10);
  if (dayOfMonth === "?" && dayOfWeek && dayOfWeek !== "*") {
    const days = dayOfWeek.split(",");
    return {
      ...defaultScheduleDraft,
      preset: days.length > 1 ? "selected-days" : "weekly",
      minute: parsedMinute || 0,
      hour: parsedHour || 0,
      dayOfWeek: days[0],
      selectedDays: days,
      cronExpression
    };
  }
  if (hour.startsWith("0/")) {
    return { ...defaultScheduleDraft, preset: "every-hours", minute: parsedMinute || 0, everyHours: Number.parseInt(hour.slice(2), 10) || 6, cronExpression };
  }
  if (dayOfMonth === "*" && dayOfWeek === "?") {
    return { ...defaultScheduleDraft, preset: "daily", minute: parsedMinute || 0, hour: parsedHour || 0, cronExpression };
  }
  if (dayOfWeek === "?") {
    return { ...defaultScheduleDraft, preset: "monthly", minute: parsedMinute || 0, hour: parsedHour || 0, dayOfMonth: Number.parseInt(dayOfMonth, 10) || 1, cronExpression };
  }
  return { ...defaultScheduleDraft, preset: "advanced", cronExpression };
}

function labelForDay(value: string) {
  return dayOptions.find((day) => day.value === value)?.label ?? value;
}

function clamp(value: number, min: number, max: number) {
  return Math.max(min, Math.min(max, Number.isFinite(value) ? value : min));
}

function pad(value: number) {
  return `${clamp(value, 0, 59)}`.padStart(2, "0");
}
