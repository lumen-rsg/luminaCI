export const shortId = (value?: string) => value ? value.slice(0, 8) : "—";
export const dateTime = (value?: string) => value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
  : "—";
export const timeAgo = (value?: string) => {
  if (!value) return "—";
  const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
  const minutes = Math.round(seconds / 60);
  if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
  return formatter.format(Math.round(hours / 24), "day");
};
export const fileSize = (bytes = 0) => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
};
export const hostName = (value?: string) => {
  if (!value) return "Manual specification";
  try { return new URL(value).hostname; } catch { return value; }
};
export const tone = (status?: string) => {
  const value = status?.toLowerCase() ?? "";
  if (["success", "completed", "active", "ready", "signed"].includes(value)) return "success";
  if (["failed", "error", "disabled", "critical"].includes(value)) return "danger";
  if (["building", "running", "fetching", "inprogress"].includes(value)) return "info";
  if (["queued", "pending", "paused", "warning"].includes(value)) return "warning";
  return "neutral";
};
