export function nameOf(items: Array<{ id: string; name: string }> | undefined, id: string) {
  return items?.find((item) => item.id === id)?.name ?? id;
}

export function presetLabel(value: string) {
  return value.split("-").map((part) => part[0].toUpperCase() + part.slice(1)).join(" ");
}

export function formatTime(value?: string | null) {
  if (!value) return "never";
  return new Date(value).toLocaleString();
}
export function formatCompletionTime(completedAt?: string | null, startedAt?: string | null, fallbackStartAt?: string | null) {
  if (!completedAt) return "not completed";
  const duration = formatDuration(startedAt ?? fallbackStartAt, completedAt);
  return duration ? `${formatTime(completedAt)} (took ${duration})` : formatTime(completedAt);
}

export function formatDurationSeconds(seconds?: number | null) {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return "none";
  const totalMinutes = Math.max(1, Math.round(seconds / 60));
  const days = Math.floor(totalMinutes / 1440);
  const hours = Math.floor((totalMinutes % 1440) / 60);
  const minutes = totalMinutes % 60;
  const parts: string[] = [];
  if (days > 0) parts.push(`${days} day${days === 1 ? "" : "s"}`);
  if (hours > 0) parts.push(`${hours} hour${hours === 1 ? "" : "s"}`);
  if (minutes > 0 || parts.length === 0) parts.push(`${minutes} minute${minutes === 1 ? "" : "s"}`);
  return parts.join(" and ");
}

function formatDuration(start?: string | null, end?: string | null) {
  if (!start || !end) return null;
  const milliseconds = new Date(end).getTime() - new Date(start).getTime();
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return null;
  const totalMinutes = Math.max(1, Math.round(milliseconds / 60000));
  const days = Math.floor(totalMinutes / 1440);
  const hours = Math.floor((totalMinutes % 1440) / 60);
  const minutes = totalMinutes % 60;
  const parts: string[] = [];
  if (days > 0) parts.push(`${days} day${days === 1 ? "" : "s"}`);
  if (hours > 0) parts.push(`${hours} hour${hours === 1 ? "" : "s"}`);
  if (minutes > 0 || parts.length === 0) parts.push(`${minutes} minute${minutes === 1 ? "" : "s"}`);
  return parts.join(" and ");
}


export function formatBytes(value?: number | null) {
  if (value === null || value === undefined || !Number.isFinite(value)) return "unknown";
  if (value === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  const exponent = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  const amount = value / Math.pow(1024, exponent);
  const digits = amount >= 10 || exponent === 0 ? 0 : 1;
  return `${amount.toFixed(digits)} ${units[exponent]}`;
}

