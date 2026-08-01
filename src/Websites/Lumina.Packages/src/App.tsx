import {
  ArrowDownToLine,
  ArrowRight,
  Boxes,
  Check,
  ChevronDown,
  Clipboard,
  Code2,
  Cpu,
  Download,
  ExternalLink,
  FileKey2,
  HardDrive,
  Moon,
  PackageCheck,
  RefreshCw,
  Search,
  ShieldCheck,
  Sun,
  TerminalSquare
} from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { formatBytes, loadPackageIndex, type PackageEntry } from "./repository";

type Theme = "dark" | "light";
type SortMode = "name" | "newest" | "size";

const setupCommand = "sudo curl -fsSL https://packages.lumina.1t.ru/lumina.repo -o /etc/yum.repos.d/lumina.repo";
const signingFingerprint = "B70C 524E C6DE A9BB 5267 6D26 A6B2 7089 9D90 3626";

function preferredTheme(): Theme {
  const saved = window.localStorage.getItem("lumina-packages-theme");
  if (saved === "dark" || saved === "light") return saved;
  return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

function CopyButton({ value, label = "Copy" }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(value);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1800);
  }

  return (
    <button className="copy-button" type="button" onClick={copy} aria-label={`${label}: ${value}`}>
      {copied ? <Check aria-hidden="true" /> : <Clipboard aria-hidden="true" />}
      <span>{copied ? "Copied" : label}</span>
    </button>
  );
}

function Brand() {
  return (
    <a className="brand" href="#top" aria-label="Lumina Packages home">
      <span className="brand__mark"><Boxes aria-hidden="true" /></span>
      <span className="brand__copy"><strong>Lumina</strong><small>Package network</small></span>
    </a>
  );
}

function PackageExplorer({ packages, loading, error, onRetry }: {
  packages: PackageEntry[];
  loading: boolean;
  error: string | null;
  onRetry: () => void;
}) {
  const [query, setQuery] = useState("");
  const [architecture, setArchitecture] = useState("all");
  const [sort, setSort] = useState<SortMode>("name");

  const architectures = useMemo(
    () => [...new Set(packages.map(item => item.architecture))].sort(),
    [packages]
  );

  const visiblePackages = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase();
    const filtered = packages.filter(item => {
      const matchesArchitecture = architecture === "all" || item.architecture === architecture;
      const matchesQuery = normalized.length === 0 ||
        `${item.name} ${item.version} ${item.release} ${item.architecture}`.toLocaleLowerCase().includes(normalized);
      return matchesArchitecture && matchesQuery;
    });

    return filtered.sort((left, right) => {
      if (sort === "size") return right.size - left.size;
      if (sort === "newest") {
        return (right.modifiedAt ?? "").localeCompare(left.modifiedAt ?? "") || left.name.localeCompare(right.name);
      }
      return left.name.localeCompare(right.name) || right.version.localeCompare(left.version, undefined, { numeric: true });
    });
  }, [architecture, packages, query, sort]);

  return (
    <section className="packages-section section-shell" id="packages" aria-labelledby="packages-title">
      <div className="section-heading">
        <div>
          <span className="eyebrow">Repository explorer</span>
          <h2 id="packages-title">Find the exact build you need.</h2>
          <p>Every link is a direct, cache-friendly RPM download from the public repository.</p>
        </div>
        {!loading && !error && <span className="result-count" aria-live="polite">{visiblePackages.length} packages</span>}
      </div>

      <div className="explorer-toolbar" role="search">
        <label className="search-field">
          <Search aria-hidden="true" />
          <span className="sr-only">Search packages</span>
          <input
            value={query}
            onChange={event => setQuery(event.target.value)}
            placeholder="Search name, version, or architecture"
            type="search"
          />
        </label>
        <label className="select-field">
          <span className="sr-only">Filter by architecture</span>
          <Cpu aria-hidden="true" />
          <select value={architecture} onChange={event => setArchitecture(event.target.value)}>
            <option value="all">All architectures</option>
            {architectures.map(item => <option key={item} value={item}>{item}</option>)}
          </select>
          <ChevronDown aria-hidden="true" />
        </label>
        <label className="select-field">
          <span className="sr-only">Sort packages</span>
          <ArrowDownToLine aria-hidden="true" />
          <select value={sort} onChange={event => setSort(event.target.value as SortMode)}>
            <option value="name">Sort by name</option>
            <option value="newest">Newest first</option>
            <option value="size">Largest first</option>
          </select>
          <ChevronDown aria-hidden="true" />
        </label>
      </div>

      <div className="package-panel" aria-busy={loading}>
        {loading && (
          <div className="loading-state" role="status">
            <span className="loading-mark"><RefreshCw aria-hidden="true" /></span>
            <div><strong>Reading repository metadata</strong><span>Discovering published architectures and RPMs…</span></div>
          </div>
        )}
        {error && (
          <div className="error-state" role="alert">
            <div><strong>Repository index unavailable</strong><span>{error}</span></div>
            <button className="button button--secondary" type="button" onClick={onRetry}><RefreshCw aria-hidden="true" />Retry</button>
          </div>
        )}
        {!loading && !error && visiblePackages.length === 0 && (
          <div className="empty-state"><PackageCheck aria-hidden="true" /><strong>No matching packages</strong><span>Try another name or architecture.</span></div>
        )}
        {!loading && !error && visiblePackages.length > 0 && (
          <div className="package-table-wrap">
            <table>
              <thead>
                <tr><th scope="col">Package</th><th scope="col">Version</th><th scope="col">Architecture</th><th scope="col">Size</th><th scope="col"><span className="sr-only">Download</span></th></tr>
              </thead>
              <tbody>
                {visiblePackages.map(item => (
                  <tr key={`${item.architecture}/${item.fileName}`}>
                    <th scope="row">
                      <span className="package-identity"><span className="package-icon"><Boxes aria-hidden="true" /></span><span><strong>{item.name}</strong><small>{item.fileName}</small></span></span>
                    </th>
                    <td><strong className="version">{item.version}</strong><small className="release">{item.release}</small></td>
                    <td><span className="arch-badge">{item.architecture}</span></td>
                    <td>{formatBytes(item.size)}</td>
                    <td className="download-cell"><a className="download-button" href={item.url} download aria-label={`Download ${item.fileName}`}><Download aria-hidden="true" /><span>Download</span></a></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}

export default function App() {
  const [theme, setTheme] = useState<Theme>(preferredTheme);
  const [packages, setPackages] = useState<PackageEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    loadPackageIndex(controller.signal)
      .then(setPackages)
      .catch(reason => {
        if (reason instanceof DOMException && reason.name === "AbortError") return;
        setError(reason instanceof Error ? reason.message : "The repository index could not be loaded.");
      })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, []);

  useEffect(() => load(), [load]);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    window.localStorage.setItem("lumina-packages-theme", theme);
  }, [theme]);

  const stats = useMemo(() => ({
    architectures: new Set(packages.map(item => item.architecture)).size,
    count: packages.length,
    size: packages.reduce((total, item) => total + item.size, 0)
  }), [packages]);

  return (
    <>
      <a className="skip-link" href="#main-content">Skip to package explorer</a>
      <header className="site-header" id="top">
        <div className="header-inner">
          <Brand />
          <nav aria-label="Primary navigation">
            <a href="#repositories">Repositories</a>
            <a href="#packages">Packages</a>
            <a href="#setup">Set up DNF</a>
          </nav>
          <div className="header-actions">
            <a className="console-link" href="https://console.lumina.1t.ru" target="_blank" rel="noreferrer">LuminaCI <ExternalLink aria-hidden="true" /></a>
            <button className="theme-button" type="button" onClick={() => setTheme(value => value === "dark" ? "light" : "dark")} aria-label={`Switch to ${theme === "dark" ? "light" : "dark"} theme`}>
              {theme === "dark" ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
            </button>
          </div>
        </div>
      </header>

      <main id="main-content">
        <section className="hero section-shell" aria-labelledby="hero-title">
          <div className="hero__content">
            <span className="availability"><i aria-hidden="true" />Public repository online</span>
            <h1 id="hero-title">Packages, signed<br />and ready.</h1>
            <p>Production RPMs for Lumina Linux—built by LuminaCI, verified on native Fedora workers, and delivered directly over HTTPS.</p>
            <div className="hero__actions">
              <a className="button button--primary" href="#packages">Browse packages <ArrowRight aria-hidden="true" /></a>
              <a className="button button--ghost" href="/lumina.repo" download><Download aria-hidden="true" />Download repo file</a>
            </div>
            <div className="trust-row" aria-label="Repository guarantees">
              <span><ShieldCheck aria-hidden="true" />GPG-signed RPMs</span>
              <span><Cpu aria-hidden="true" />Native architecture builds</span>
              <span><PackageCheck aria-hidden="true" />Atomic publishing</span>
            </div>
          </div>
          <div className="hero__visual" aria-hidden="true">
            <div className="orbit orbit--outer"><span /><span /><span /></div>
            <div className="orbit orbit--inner"><span /><span /></div>
            <div className="package-core"><Boxes /><small>RPM</small></div>
            <div className="signal-card signal-card--top"><ShieldCheck /><span><strong>Verified</strong><small>signature gate</small></span></div>
            <div className="signal-card signal-card--bottom"><HardDrive /><span><strong>Immutable</strong><small>artifact delivery</small></span></div>
          </div>
        </section>

        <section className="stats-ribbon section-shell" aria-label="Repository statistics">
          <article><PackageCheck aria-hidden="true" /><span><strong>{loading ? "—" : stats.count}</strong><small>Published RPMs</small></span></article>
          <article><Cpu aria-hidden="true" /><span><strong>{loading ? "—" : stats.architectures}</strong><small>Architectures</small></span></article>
          <article><HardDrive aria-hidden="true" /><span><strong>{loading ? "—" : formatBytes(stats.size)}</strong><small>Package payload</small></span></article>
          <article><ShieldCheck aria-hidden="true" /><span><strong>RSA 4096</strong><small>Release signing</small></span></article>
        </section>

        <section className="repositories section-shell" id="repositories" aria-labelledby="repositories-title">
          <div className="section-heading">
            <div><span className="eyebrow">Package network</span><h2 id="repositories-title">One reliable origin.</h2><p>Stable paths for current Lumina releases and compatibility mirrors.</p></div>
          </div>
          <div className="repository-grid">
            <article className="repository-card repository-card--featured">
              <header><span className="repository-icon"><Boxes aria-hidden="true" /></span><span className="status-pill"><i />Recommended</span></header>
              <h3>Lumen</h3><p>Signed LuminaCI releases for supported architectures, including the coherent Jetson R39.2 stack.</p>
              <footer><code>/lumen/$basearch</code><a href="/lumen/">Open index <ArrowRight aria-hidden="true" /></a></footer>
            </article>
            <article className="repository-card">
              <header><span className="repository-icon repository-icon--blue"><Code2 aria-hidden="true" /></span><span className="status-pill status-pill--quiet">Compatibility</span></header>
              <h3>Core &amp; extra</h3><p>Established package trees retained for existing Lumina systems and package consumers.</p>
              <footer><code>/core · /extra</code><a href="/core/">Browse core <ArrowRight aria-hidden="true" /></a></footer>
            </article>
            <article className="repository-card">
              <header><span className="repository-icon repository-icon--amber"><RefreshCw aria-hidden="true" /></span><span className="status-pill status-pill--quiet">Archive</span></header>
              <h3>Releases &amp; updates</h3><p>Versioned release trees and update channels for reproducible system provisioning.</p>
              <footer><code>/releases · /updates</code><a href="/releases/">Browse releases <ArrowRight aria-hidden="true" /></a></footer>
            </article>
          </div>
        </section>

        <PackageExplorer packages={packages} loading={loading} error={error} onRetry={load} />

        <section className="setup-section section-shell" id="setup" aria-labelledby="setup-title">
          <div className="setup-copy">
            <span className="eyebrow">Fedora setup</span>
            <h2 id="setup-title">Connect in one command.</h2>
            <p>Install the repository definition, then use DNF normally. Package signature checks stay enabled by default.</p>
            <div className="setup-links">
              <a href="/lumina.repo" download><TerminalSquare aria-hidden="true" />Repository file</a>
              <a href="/RPM-GPG-KEY-lumina" download><FileKey2 aria-hidden="true" />Public signing key</a>
            </div>
          </div>
          <div className="terminal-card">
            <header><span><i /><i /><i /></span><small>terminal</small></header>
            <div className="terminal-line"><span>$</span><code>{setupCommand}</code><CopyButton value={setupCommand} /></div>
            <div className="fingerprint"><ShieldCheck aria-hidden="true" /><span><small>Signing fingerprint</small><code>{signingFingerprint}</code></span></div>
          </div>
        </section>
      </main>

      <footer className="site-footer">
        <div className="section-shell"><Brand /><p>Public package infrastructure for Lumina Linux.</p><span>Built and verified by <a href="https://console.lumina.1t.ru">LuminaCI</a>.</span></div>
      </footer>
    </>
  );
}
