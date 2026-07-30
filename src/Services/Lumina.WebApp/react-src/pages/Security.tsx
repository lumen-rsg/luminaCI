import { Fingerprint, KeyRound, LockKeyhole, Plus, RefreshCw, ScanSearch, ShieldAlert, ShieldCheck } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, StatCard, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime, shortId } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import { usePreferences } from "../i18n/PreferencesContext";
import { useAutoRefresh } from "../lib/useAutoRefresh";

export function Security() {
  const { t, locale } = usePreferences();
  const state = useAsync(async () => {
    const [scans, hashes, keys] = await Promise.all([api.scans(), api.hashes(), api.keys()]);
    return { scans: scans.scans ?? [], hashes: hashes.hashes ?? [], keys };
  }, []);
  useAutoRefresh(state.reload);
  const [keyModal, setKeyModal] = useState(false);
  const [message, setMessage] = useState("");
  const scans = state.data?.scans ?? [];
  const critical = scans.reduce((sum, scan) => sum + scan.criticalCount, 0);
  const high = scans.reduce((sum, scan) => sum + scan.highCount, 0);
  return <>
    <PageHeader eyebrow={t("security.eyebrow")} title={t("security.title")} description={t("security.description")} actions={<><button className="button button--primary" onClick={() => setKeyModal(true)}><Plus /> {t("security.generate")}</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> {t("common.refresh")}</button></>} />
    {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
    {state.loading ? <Loading label={t("security.loading")} /> : state.error || !state.data ? <ErrorState error={state.error} retry={state.reload} /> : <div className="page-stack">
      <section className="stats-grid stats-grid--three">
        <StatCard label={t("security.signingKeys")} value={state.data.keys.length} detail={t("security.identities")} icon={<KeyRound />} />
        <StatCard label={t("security.critical")} value={critical} detail={t("security.criticalDetail")} icon={<ShieldAlert />} tone="red" />
        <StatCard label={t("security.high")} value={high} detail={t("security.highDetail")} icon={<ScanSearch />} tone="amber" />
      </section>
      <section className="panel table-panel">
        <header className="panel__header"><div><span className="eyebrow">{t("security.intelligence")}</span><h2>{t("security.recentScans")}</h2></div><ShieldCheck /></header>
        {scans.length ? <div className="table-wrap"><table><caption className="sr-only">{t("security.scanCaption")}</caption><thead><tr><th>{t("security.artifact")}</th><th>{t("security.scanner")}</th><th>{t("common.status")}</th><th>{t("security.findings")}</th><th>{t("security.completed")}</th></tr></thead><tbody>{scans.map(scan => <tr key={scan.id}><th><code>{shortId(scan.artifactId)}</code></th><td>{scan.scannerType}</td><td><Status value={scan.status} domain="scan" /></td><td><span className={scan.criticalCount ? "risk risk--critical" : "risk"}>{t("security.criticalCount", { count: scan.criticalCount })}</span><span className={scan.highCount ? "risk risk--high" : "risk"}>{t("security.highCount", { count: scan.highCount })}</span></td><td>{dateTime(scan.completedAt || scan.createdAt, locale)}</td></tr>)}</tbody></table></div> : <Empty title={t("security.noScans")} detail={t("security.noScansDetail")} />}
      </section>
      <section className="panel table-panel">
        <header className="panel__header"><div><span className="eyebrow">{t("security.integrity")}</span><h2>{t("security.hashLedger")}</h2></div><Fingerprint /></header>
        {state.data.hashes.length ? <div className="table-wrap"><table><caption className="sr-only">{t("security.hashCaption")}</caption><thead><tr><th>{t("security.artifact")}</th><th>SHA-256</th><th>{t("security.computed")}</th></tr></thead><tbody>{state.data.hashes.map(hash => <tr key={`${hash.artifactId}-${hash.computedAt}`}><th><code>{shortId(hash.artifactId)}</code></th><td><code className="digest">{hash.hashSha256}</code></td><td>{dateTime(hash.computedAt, locale)}</td></tr>)}</tbody></table></div> : <Empty title={t("security.noHashes")} detail={t("security.noHashesDetail")} />}
      </section>
    </div>}
    {keyModal && <KeyModal onClose={() => setKeyModal(false)} onSaved={() => { setKeyModal(false); setMessage(t("security.requested")); state.reload(); }} />}
  </>;
}

function KeyModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { t } = usePreferences();
  const [keyName, setKeyName] = useState("");
  const [email, setEmail] = useState("");
  const [error, setError] = useState("");
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    try { await api.generateKey(keyName, email); onSaved(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : t("security.error")); }
  };
  return <Modal title={t("security.generate")} onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label className="span-2">{t("security.keyName")}<input required value={keyName} onChange={event => setKeyName(event.target.value)} placeholder={t("security.keyPlaceholder")} /></label>
    <label className="span-2">{t("security.email")}<input required type="email" value={email} onChange={event => setEmail(event.target.value)} placeholder="release@example.com" /></label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>{t("common.cancel")}</button><button className="button button--primary"><LockKeyhole /> {t("security.generate")}</button></footer>
  </form></Modal>;
}
