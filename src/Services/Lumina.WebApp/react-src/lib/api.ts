import type {
  ApiResponse, Build, HashRecord, Paginated, Pipeline, Repository, Scan, SourcePackage
} from "../types";

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

let refreshPromise: Promise<boolean> | null = null;

async function refreshSession() {
  refreshPromise ??= fetch("/api/auth/refresh", {
    method: "POST",
    credentials: "same-origin"
  }).then(response => response.ok).catch(() => false).finally(() => {
    refreshPromise = null;
  });
  return refreshPromise;
}

async function request<T>(path: string, init: RequestInit = {}, retry = true, transientAttempt = 0): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.body && !(init.body instanceof FormData)) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...init, headers, credentials: "same-origin" });

  if ([502, 503, 504].includes(response.status) && (!init.method || init.method === "GET") && transientAttempt < 2) {
    await new Promise(resolve => window.setTimeout(resolve, 500 * (2 ** transientAttempt)));
    return request<T>(path, init, retry, transientAttempt + 1);
  }

  if (response.status === 401 && retry && !path.startsWith("/api/auth/")) {
    if (await refreshSession()) return request<T>(path, init, false);
    window.dispatchEvent(new Event("lumina:unauthorized"));
  }

  if (!response.ok) {
    let message = response.statusText || "Request failed";
    try {
      const body = await response.json();
      message = body.error || body.message || message;
    } catch { /* preserve status text */ }
    throw new ApiError(response.status, message);
  }

  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

const unwrap = <T>(response: ApiResponse<T>) => response.data;

export const authApi = {
  me: () => request<{ username: string; role: string }>("/api/auth/me", {}, false),
  login: (username: string, password: string) =>
    request<{ username: string; role: string }>("/api/auth/login", {
      method: "POST", body: JSON.stringify({ username, password })
    }, false),
  logout: () => request<void>("/api/auth/logout", { method: "POST" }, false)
};

export const api = {
  builds: (page = 1, status = "") =>
    request<ApiResponse<Paginated<Build>>>(`/api/builds?page=${page}&pageSize=20${status ? `&status=${status}` : ""}`).then(unwrap),
  build: (id: string) => request<ApiResponse<Build>>(`/api/builds/${id}`).then(unwrap),
  buildLogs: (id: string) => request<ApiResponse<string>>(`/api/builds/${id}/logs`).then(unwrap),
  buildStats: () => request<ApiResponse<{ totalCount: number; successfulCount: number; failedCount: number }>>("/api/builds/stats").then(unwrap),
  buildQueue: () => request<ApiResponse<{ queued: Build[]; running: Build[]; queuedCount: number; runningCount: number }>>("/api/builds/queue").then(unwrap),
  cancelBuild: (id: string) => request(`/api/builds/${id}/cancel`, { method: "POST" }),
  clearBuildQueue: () => request("/api/builds/queue/clear", { method: "DELETE" }),

  pipelines: (page = 1, search = "") =>
    request<ApiResponse<Paginated<Pipeline>>>(`/api/pipelines?page=${page}&pageSize=20${search ? `&search=${encodeURIComponent(search)}` : ""}`).then(unwrap),
  pipeline: (id: string) => request<ApiResponse<Pipeline>>(`/api/pipelines/${id}`).then(unwrap),
  createPipeline: (body: unknown) => request<ApiResponse<Pipeline>>("/api/pipelines", { method: "POST", body: JSON.stringify(body) }).then(unwrap),
  updatePipeline: (id: string, body: unknown) => request<ApiResponse<Pipeline>>(`/api/pipelines/${id}`, { method: "PUT", body: JSON.stringify(body) }).then(unwrap),
  deletePipeline: (id: string) => request(`/api/pipelines/${id}`, { method: "DELETE" }),
  triggerPipeline: (id: string) => request<ApiResponse<Build>>(`/api/pipelines/${id}/trigger-auto`, {
    method: "POST", body: JSON.stringify({ triggeredBy: "web-console" })
  }).then(unwrap),

  repositories: () => request<ApiResponse<{ repositories: Repository[] }>>("/api/repository").then(unwrap),
  createRepository: (body: unknown) => request<ApiResponse<Repository>>("/api/repository", { method: "POST", body: JSON.stringify(body) }).then(unwrap),
  syncRepository: (id: string) => request("/api/repository/sync", { method: "POST", body: JSON.stringify({ repositoryId: id }) }),

  scans: (page = 1) => request<ApiResponse<Paginated<Scan>>>(`/api/scanner/scans?page=${page}&pageSize=20`).then(unwrap),
  hashes: (page = 1) => request<ApiResponse<Paginated<HashRecord>>>(`/api/security/hashes?page=${page}&pageSize=20`).then(unwrap),
  keys: () => request<ApiResponse<unknown[]>>("/api/security/keys").then(unwrap),
  generateKey: (keyName: string, email: string) => request("/api/security/keys/generate", {
    method: "POST", body: JSON.stringify({ keyName, email })
  }),

  sources: () => request<ApiResponse<{ packages: SourcePackage[]; totalCount: number }>>("/api/sources").then(unwrap),
  saveSource: (slug: string | undefined, body: unknown) => request(`/api/sources${slug ? `/${encodeURIComponent(slug)}` : ""}`, {
    method: slug ? "PUT" : "POST", body: JSON.stringify(body)
  }),
  fetchSource: (name: string) => request(`/api/sources/${encodeURIComponent(name)}/fetch`, { method: "POST" }),
  disableSource: (name: string, revision: number) =>
    request(`/api/sources/${encodeURIComponent(name)}?expectedRevision=${revision}`, { method: "DELETE" })
};
