import { Fingerprint, KeyRound, LockKeyhole, Plus, RefreshCw, ScanSearch, ShieldAlert, ShieldCheck } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, StatCard, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime, shortId } from "../lib/format";
import { useAsync } from "../lib/useAsync";

export function Security() {
  const state = useAsync(async () => {
    const [scans, hashes, keys] = await Promise.all([api.scans(), api.hashes(), api.keys()]);
    return { scans: scans.scans ?? [], hashes: hashes.hashes ?? [], keys };
  }, []);
  const [keyModal, setKeyModal] = useState(false);
  const [message, setMessage] = useState("");
  const scans = state.data?.scans ?? [];
  const critical = scans.reduce((sum, scan) => sum + scan.criticalCount, 0);
  const high = scans.reduce((sum, scan) => sum + scan.highCount, 0);
  return <>
    <PageHeader eyebrow="Supply chain trust" title="Security" description="Track signatures, integrity records, and vulnerability intelligence." actions={<><button className="button button--primary" onClick={() => setKeyModal(true)}><Plus /> Generate key</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> Refresh</button></>} />
    {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
    {state.loading ? <Loading label="Loading security posture" /> : state.error || !state.data ? <ErrorState error={state.error} retry={state.reload} /> : <div className="page-stack">
      <section className="stats-grid stats-grid--three">
        <StatCard label="Signing keys" value={state.data.keys.length} detail="Managed trust identities" icon={<KeyRound />} />
        <StatCard label="Critical CVEs" value={critical} detail="Require immediate attention" icon={<ShieldAlert />} tone="red" />
        <StatCard label="High CVEs" value={high} detail="Across recent scans" icon={<ScanSearch />} tone="amber" />
      </section>
      <section className="panel table-panel">
        <header className="panel__header"><div><span className="eyebrow">Vulnerability intelligence</span><h2>Recent scans</h2></div><ShieldCheck /></header>
        {scans.length ? <div className="table-wrap"><table><caption className="sr-only">Recent vulnerability scans</caption><thead><tr><th>Artifact</th><th>Scanner</th><th>Status</th><th>Findings</th><th>Completed</th></tr></thead><tbody>{scans.map(scan => <tr key={scan.id}><th><code>{shortId(scan.artifactId)}</code></th><td>{scan.scannerType}</td><td><Status value={scan.status} domain="scan" /></td><td><span className={scan.criticalCount ? "risk risk--critical" : "risk"}>{scan.criticalCount} critical</span><span className={scan.highCount ? "risk risk--high" : "risk"}>{scan.highCount} high</span></td><td>{dateTime(scan.completedAt || scan.createdAt)}</td></tr>)}</tbody></table></div> : <Empty title="No scans recorded" detail="Vulnerability results appear after an artifact reaches the scan stage." />}
      </section>
      <section className="panel table-panel">
        <header className="panel__header"><div><span className="eyebrow">Artifact integrity</span><h2>Hash ledger</h2></div><Fingerprint /></header>
        {state.data.hashes.length ? <div className="table-wrap"><table><caption className="sr-only">Artifact hash records</caption><thead><tr><th>Artifact</th><th>SHA-256</th><th>Computed</th></tr></thead><tbody>{state.data.hashes.map(hash => <tr key={`${hash.artifactId}-${hash.computedAt}`}><th><code>{shortId(hash.artifactId)}</code></th><td><code className="digest">{hash.hashSha256}</code></td><td>{dateTime(hash.computedAt)}</td></tr>)}</tbody></table></div> : <Empty title="No hash records" detail="Verified digests will appear here after artifacts are produced." />}
      </section>
    </div>}
    {keyModal && <KeyModal onClose={() => setKeyModal(false)} onSaved={() => { setKeyModal(false); setMessage("Signing key generation requested."); state.reload(); }} />}
  </>;
}

function KeyModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [keyName, setKeyName] = useState("");
  const [email, setEmail] = useState("");
  const [error, setError] = useState("");
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    try { await api.generateKey(keyName, email); onSaved(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Unable to generate key."); }
  };
  return <Modal title="Generate signing key" onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label className="span-2">Key name<input required value={keyName} onChange={event => setKeyName(event.target.value)} placeholder="Lumina release signing" /></label>
    <label className="span-2">Identity email<input required type="email" value={email} onChange={event => setEmail(event.target.value)} placeholder="release@example.com" /></label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>Cancel</button><button className="button button--primary"><LockKeyhole /> Generate key</button></footer>
  </form></Modal>;
}
