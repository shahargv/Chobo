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
