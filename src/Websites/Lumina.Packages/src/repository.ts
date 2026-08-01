export type AutoIndexEntry = {
  name: string;
  type: "file" | "directory" | string;
  mtime?: string;
  size?: number;
};

export type PackageEntry = {
  architecture: string;
  fileName: string;
  modifiedAt: string | null;
  name: string;
  release: string;
  size: number;
  url: string;
  version: string;
};

const supportedArchitectures = new Set(["aarch64", "x86_64", "noarch"]);

export function parseRpmFileName(fileName: string, architecture: string, entry: AutoIndexEntry): PackageEntry | null {
  const match = /^(.+)-([^-]+)-([^-]+)\.([^.]+)\.rpm$/.exec(fileName);
  if (!match) return null;

  const [, name, version, release, fileArchitecture] = match;
  if (fileArchitecture !== architecture && fileArchitecture !== "noarch") return null;

  const parsedDate = entry.mtime ? new Date(entry.mtime) : null;
  const modifiedAt = parsedDate && !Number.isNaN(parsedDate.valueOf()) && parsedDate.getUTCFullYear() > 2000
    ? parsedDate.toISOString()
    : null;

  return {
    architecture: fileArchitecture,
    fileName,
    modifiedAt,
    name,
    release,
    size: typeof entry.size === "number" ? entry.size : 0,
    url: `/lumen/${encodeURIComponent(architecture)}/${encodeURIComponent(fileName)}`,
    version
  };
}

export function collectPackages(architecture: string, entries: AutoIndexEntry[]): PackageEntry[] {
  return entries
    .filter(entry => entry.type === "file" && entry.name.endsWith(".rpm"))
    .map(entry => parseRpmFileName(entry.name, architecture, entry))
    .filter((entry): entry is PackageEntry => entry !== null);
}

async function fetchListing(path: string, signal?: AbortSignal): Promise<AutoIndexEntry[]> {
  const response = await fetch(path, { headers: { Accept: "application/json" }, signal });
  if (!response.ok) throw new Error(`Repository index returned HTTP ${response.status}.`);
  const body: unknown = await response.json();
  if (!Array.isArray(body)) throw new Error("Repository index returned an invalid response.");
  return body as AutoIndexEntry[];
}

export async function loadPackageIndex(signal?: AbortSignal): Promise<PackageEntry[]> {
  const root = await fetchListing("/api/package-index/", signal);
  const architectures = root
    .filter(entry => entry.type === "directory" && supportedArchitectures.has(entry.name))
    .map(entry => entry.name)
    .sort();

  if (architectures.length === 0) throw new Error("No published architectures are available.");

  const listings = await Promise.all(
    architectures.map(async architecture => ({
      architecture,
      entries: await fetchListing(`/api/package-index/${encodeURIComponent(architecture)}/`, signal)
    }))
  );

  return listings.flatMap(listing => collectPackages(listing.architecture, listing.entries));
}

export function formatBytes(bytes: number): string {
  if (bytes <= 0) return "0 B";
  const units = ["B", "KiB", "MiB", "GiB"];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / 1024 ** index;
  return `${new Intl.NumberFormat("en", { maximumFractionDigits: index === 0 ? 0 : 1 }).format(value)} ${units[index]}`;
}
