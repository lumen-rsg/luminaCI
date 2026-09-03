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
import {
  formatPackageCount,
  resolveLanguage,
  translations,
  type Copy,
  type Language
} from "./localization";
import { formatBytes, loadPackageIndex, type PackageEntry } from "./repository";

type Theme = "dark" | "light";
type SortMode = "name" | "newest" | "size";

const setupCommand = "sudo curl -fsSL https://packages.lumina.1t.ru/lumina.repo -o /etc/yum.repos.d/lumina.repo";
const signingFingerprint = "EBE3 9C73 6CAC 92CE C213 9DC7 6206 7582 4776 D3D7";

function preferredTheme(): Theme {
  const saved = window.localStorage.getItem("lumina-packages-theme");
  if (saved === "dark" || saved === "light") return saved;
  return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

function preferredLanguage(): Language {
  const browserLanguages = navigator.languages.length > 0 ? navigator.languages : [navigator.language];
  return resolveLanguage(window.localStorage.getItem("lumina-packages-language"), browserLanguages);
}

function CopyButton({ value, label, copiedLabel }: { value: string; label: string; copiedLabel: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(value);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1800);
  }

  return (
    <button className="copy-button" type="button" onClick={copy} aria-label={`${label}: ${value}`}>
      {copied ? <Check aria-hidden="true" /> : <Clipboard aria-hidden="true" />}
      <span>{copied ? copiedLabel : label}</span>
    </button>
  );
}

function Brand({ copy }: { copy: Copy }) {
  return (
    <a className="brand" href="#top" aria-label={copy.brandHome}>
      <span className="brand__mark"><Boxes aria-hidden="true" /></span>
      <span className="brand__copy"><strong>Lumina</strong><small>{copy.brandTagline}</small></span>
    </a>
  );
}

function LanguageSwitcher({ language, copy, onChange }: {
  language: Language;
  copy: Copy;
  onChange: (language: Language) => void;
}) {
  return (
    <div className="language-switcher" role="group" aria-label={copy.languageSelector}>
      <button
        type="button"
        lang="en"
        aria-label={copy.selectEnglish}
        aria-pressed={language === "en"}
        onClick={() => onChange("en")}
      >
        EN
      </button>
      <button
        type="button"
        lang="ru"
        aria-label={copy.selectRussian}
        aria-pressed={language === "ru"}
        onClick={() => onChange("ru")}
      >
        RU
      </button>
    </div>
  );
}

function PackageExplorer({ packages, loading, error, onRetry, language, copy }: {
  packages: PackageEntry[];
  loading: boolean;
  error: boolean;
  onRetry: () => void;
  language: Language;
  copy: Copy;
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
          <span className="eyebrow">{copy.explorerEyebrow}</span>
          <h2 id="packages-title">{copy.explorerTitle}</h2>
          <p>{copy.explorerDescription}</p>
        </div>
        {!loading && !error && (
          <span className="result-count" aria-live="polite">{formatPackageCount(visiblePackages.length, language)}</span>
        )}
      </div>

      <div className="explorer-toolbar" role="search">
        <label className="search-field">
          <Search aria-hidden="true" />
          <span className="sr-only">{copy.searchPackages}</span>
          <input
            value={query}
            onChange={event => setQuery(event.target.value)}
            placeholder={copy.searchPlaceholder}
            type="search"
          />
        </label>
        <label className="select-field">
          <span className="sr-only">{copy.filterArchitecture}</span>
          <Cpu aria-hidden="true" />
          <select value={architecture} onChange={event => setArchitecture(event.target.value)}>
            <option value="all">{copy.allArchitectures}</option>
            {architectures.map(item => <option key={item} value={item}>{item}</option>)}
          </select>
          <ChevronDown aria-hidden="true" />
        </label>
        <label className="select-field">
          <span className="sr-only">{copy.sortPackages}</span>
          <ArrowDownToLine aria-hidden="true" />
          <select value={sort} onChange={event => setSort(event.target.value as SortMode)}>
            <option value="name">{copy.sortByName}</option>
            <option value="newest">{copy.newestFirst}</option>
            <option value="size">{copy.largestFirst}</option>
          </select>
          <ChevronDown aria-hidden="true" />
        </label>
      </div>

      <div className="package-panel" aria-busy={loading}>
        {loading && (
          <div className="loading-state" role="status">
            <span className="loading-mark"><RefreshCw aria-hidden="true" /></span>
            <div><strong>{copy.loadingTitle}</strong><span>{copy.loadingDescription}</span></div>
          </div>
        )}
        {error && (
          <div className="error-state" role="alert">
            <div><strong>{copy.errorTitle}</strong><span>{copy.errorDescription}</span></div>
            <button className="button button--secondary" type="button" onClick={onRetry}><RefreshCw aria-hidden="true" />{copy.retry}</button>
          </div>
        )}
        {!loading && !error && visiblePackages.length === 0 && (
          <div className="empty-state"><PackageCheck aria-hidden="true" /><strong>{copy.emptyTitle}</strong><span>{copy.emptyDescription}</span></div>
        )}
        {!loading && !error && visiblePackages.length > 0 && (
          <div className="package-table-wrap">
            <table>
              <thead>
                <tr><th scope="col">{copy.columnPackage}</th><th scope="col">{copy.columnVersion}</th><th scope="col">{copy.columnArchitecture}</th><th scope="col">{copy.columnSize}</th><th scope="col"><span className="sr-only">{copy.download}</span></th></tr>
              </thead>
              <tbody>
                {visiblePackages.map(item => (
                  <tr key={`${item.architecture}/${item.fileName}`}>
                    <th scope="row">
                      <span className="package-identity"><span className="package-icon"><Boxes aria-hidden="true" /></span><span><strong>{item.name}</strong><small>{item.fileName}</small></span></span>
                    </th>
                    <td><strong className="version">{item.version}</strong><small className="release">{item.release}</small></td>
                    <td><span className="arch-badge">{item.architecture}</span></td>
                    <td>{formatBytes(item.size, language)}</td>
                    <td className="download-cell"><a className="download-button" href={item.url} download aria-label={`${copy.download}: ${item.fileName}`}><Download aria-hidden="true" /><span>{copy.download}</span></a></td>
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
  const [language, setLanguage] = useState<Language>(preferredLanguage);
  const [packages, setPackages] = useState<PackageEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const copy = translations[language];

  const load = useCallback(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(false);
    loadPackageIndex(controller.signal)
      .then(setPackages)
      .catch(reason => {
        if (reason instanceof DOMException && reason.name === "AbortError") return;
        setError(true);
      })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, []);

  useEffect(() => load(), [load]);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    window.localStorage.setItem("lumina-packages-theme", theme);
  }, [theme]);
  useEffect(() => {
    document.documentElement.lang = language;
    document.title = copy.metaTitle;
    document.querySelector('meta[name="description"]')?.setAttribute("content", copy.metaDescription);
    window.localStorage.setItem("lumina-packages-language", language);
  }, [copy, language]);

  const stats = useMemo(() => ({
    architectures: new Set(packages.map(item => item.architecture)).size,
    count: packages.length,
    size: packages.reduce((total, item) => total + item.size, 0)
  }), [packages]);

  return (
    <>
      <a className="skip-link" href="#packages">{copy.skipToPackages}</a>
      <header className="site-header" id="top">
        <div className="header-inner">
          <Brand copy={copy} />
          <nav aria-label={copy.primaryNavigation}>
            <a href="#repositories">{copy.navRepositories}</a>
            <a href="#packages">{copy.navPackages}</a>
            <a href="#setup">{copy.navSetup}</a>
          </nav>
          <div className="header-actions">
            <a className="console-link" href="https://console.lumina.1t.ru" target="_blank" rel="noreferrer">LuminaCI <ExternalLink aria-hidden="true" /></a>
            <LanguageSwitcher language={language} copy={copy} onChange={setLanguage} />
            <button
              className="theme-button"
              type="button"
              onClick={() => setTheme(value => value === "dark" ? "light" : "dark")}
              aria-label={theme === "dark" ? copy.switchToLight : copy.switchToDark}
            >
              {theme === "dark" ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
            </button>
          </div>
        </div>
      </header>

      <main id="main-content">
        <section className="hero section-shell" aria-labelledby="hero-title">
          <div className="hero__content">
            <span className="availability"><i aria-hidden="true" />{copy.repositoryOnline}</span>
            <h1 id="hero-title">{copy.heroLineOne}<br />{copy.heroLineTwo}</h1>
            <p>{copy.heroDescription}</p>
            <div className="hero__actions">
              <a className="button button--primary" href="#packages">{copy.browsePackages} <ArrowRight aria-hidden="true" /></a>
              <a className="button button--ghost" href="/lumina.repo" download><Download aria-hidden="true" />{copy.downloadRepoFile}</a>
            </div>
            <div className="trust-row" aria-label={copy.repositoryGuarantees}>
              <span><ShieldCheck aria-hidden="true" />{copy.signedRpms}</span>
              <span><Cpu aria-hidden="true" />{copy.nativeBuilds}</span>
              <span><PackageCheck aria-hidden="true" />{copy.atomicPublishing}</span>
            </div>
          </div>
          <div className="hero__visual" aria-hidden="true">
            <div className="orbit orbit--outer"><span /><span /><span /></div>
            <div className="orbit orbit--inner"><span /><span /></div>
            <div className="package-core"><Boxes /><small>RPM</small></div>
            <div className="signal-card signal-card--top"><ShieldCheck /><span><strong>{copy.verified}</strong><small>{copy.signatureGate}</small></span></div>
            <div className="signal-card signal-card--bottom"><HardDrive /><span><strong>{copy.immutable}</strong><small>{copy.artifactDelivery}</small></span></div>
          </div>
        </section>

        <section className="stats-ribbon section-shell" aria-label={copy.repositoryStatistics}>
          <article><PackageCheck aria-hidden="true" /><span><strong>{loading ? "—" : stats.count}</strong><small>{copy.publishedRpms}</small></span></article>
          <article><Cpu aria-hidden="true" /><span><strong>{loading ? "—" : stats.architectures}</strong><small>{copy.architectures}</small></span></article>
          <article><HardDrive aria-hidden="true" /><span><strong>{loading ? "—" : formatBytes(stats.size, language)}</strong><small>{copy.packagePayload}</small></span></article>
          <article><ShieldCheck aria-hidden="true" /><span><strong>RSA 4096</strong><small>{copy.releaseSigning}</small></span></article>
        </section>

        <section className="repositories section-shell" id="repositories" aria-labelledby="repositories-title">
          <div className="section-heading">
            <div><span className="eyebrow">{copy.packageNetwork}</span><h2 id="repositories-title">{copy.repositoriesTitle}</h2><p>{copy.repositoriesDescription}</p></div>
          </div>
          <div className="repository-grid">
            <article className="repository-card repository-card--featured">
              <header><span className="repository-icon"><Boxes aria-hidden="true" /></span><span className="status-pill"><i />{copy.recommended}</span></header>
              <h3>Lumen</h3><p>{copy.lumenDescription}</p>
              <footer><code>/lumen/$basearch</code><a href="/lumen/">{copy.openIndex} <ArrowRight aria-hidden="true" /></a></footer>
            </article>
            <article className="repository-card">
              <header><span className="repository-icon repository-icon--blue"><Code2 aria-hidden="true" /></span><span className="status-pill status-pill--quiet">{copy.compatibility}</span></header>
              <h3>{copy.coreExtraTitle}</h3><p>{copy.coreExtraDescription}</p>
              <footer><code>/core · /extra</code><a href="/core/">{copy.browseCore} <ArrowRight aria-hidden="true" /></a></footer>
            </article>
            <article className="repository-card">
              <header><span className="repository-icon repository-icon--amber"><RefreshCw aria-hidden="true" /></span><span className="status-pill status-pill--quiet">{copy.archive}</span></header>
              <h3>{copy.releasesUpdatesTitle}</h3><p>{copy.releasesUpdatesDescription}</p>
              <footer><code>/releases · /updates</code><a href="/releases/">{copy.browseReleases} <ArrowRight aria-hidden="true" /></a></footer>
            </article>
          </div>
        </section>

        <PackageExplorer
          packages={packages}
          loading={loading}
          error={error}
          onRetry={load}
          language={language}
          copy={copy}
        />

        <section className="setup-section section-shell" id="setup" aria-labelledby="setup-title">
          <div className="setup-copy">
            <span className="eyebrow">{copy.setupEyebrow}</span>
            <h2 id="setup-title">{copy.setupTitle}</h2>
            <p>{copy.setupDescription}</p>
            <div className="setup-links">
              <a href="/lumina.repo" download><TerminalSquare aria-hidden="true" />{copy.repositoryFile}</a>
              <a href="/RPM-GPG-KEY-lumina" download><FileKey2 aria-hidden="true" />{copy.publicSigningKey}</a>
            </div>
          </div>
          <div className="terminal-card">
            <header><span><i /><i /><i /></span><small>{copy.terminal}</small></header>
            <div className="terminal-line"><span>$</span><code>{setupCommand}</code><CopyButton value={setupCommand} label={copy.copy} copiedLabel={copy.copied} /></div>
            <div className="fingerprint"><ShieldCheck aria-hidden="true" /><span><small>{copy.signingFingerprint}</small><code>{signingFingerprint}</code></span></div>
          </div>
        </section>
      </main>

      <footer className="site-footer">
        <div className="section-shell"><Brand copy={copy} /><p>{copy.footerDescription}</p><span>{copy.builtBy} <a href="https://console.lumina.1t.ru">LuminaCI</a>.</span></div>
      </footer>
    </>
  );
}
