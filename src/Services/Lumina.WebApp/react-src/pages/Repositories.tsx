import { Archive, Boxes, Database, FolderSync, Plus, RefreshCw } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime } from "../lib/format";
import { useAsync } from "../lib/useAsync";

export function Repositories() {
  const state = useAsync(api.repositories, []);
  const [creating, setCreating] = useState(false);
  const [message, setMessage] = useState("");
  const repos = state.data?.repositories ?? [];
  const sync = async (id: string, name: string) => {
    await api.syncRepository(id); setMessage(`${name} metadata sync started.`); state.reload();
  };
  return <>
    <PageHeader eyebrow="Package distribution" title="Repositories" description="Publish trusted RPMs into architecture-specific package channels." actions={<><button className="button button--primary" onClick={() => setCreating(true)}><Plus /> New repository</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> Refresh</button></>} />
    {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
    {state.loading ? <Loading label="Loading package repositories" /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : repos.length ? <div className="repo-grid">{repos.map(repo => <article className="repo-card" key={repo.id}>
      <header><span className="repo-card__icon"><Database /></span><Status value={repo.isActive ? "Active" : "Disabled"} /></header>
      <h2>{repo.displayName}</h2><code>{repo.name}</code>
      <div className="repo-metric"><strong>{repo.packageCount}</strong><span>packages<br />available</span><Archive /></div>
      <dl><div><dt>Distribution</dt><dd>{repo.distribution}</dd></div><div><dt>Architecture</dt><dd>{repo.arch}</dd></div><div><dt>Created</dt><dd>{dateTime(repo.createdAt)}</dd></div></dl>
      <footer><button className="button button--secondary" onClick={() => void sync(repo.id, repo.displayName)}><FolderSync /> Sync metadata</button></footer>
    </article>)}</div> : <Empty title="No repositories configured" detail="Create a repository to begin publishing verified packages." action={<button className="button button--primary" onClick={() => setCreating(true)}><Plus /> Create repository</button>} />}
    {creating && <RepositoryModal onClose={() => setCreating(false)} onSaved={() => { setCreating(false); setMessage("Repository created."); state.reload(); }} />}
  </>;
}

function RepositoryModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [draft, setDraft] = useState({ name: "", displayName: "", basePath: "/var/lib/lumina/repositories", arch: "aarch64", distribution: "fedora-44" });
  const [error, setError] = useState("");
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError("");
    try { await api.createRepository({ ...draft, createdBy: "web-console" }); onSaved(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Unable to create repository."); }
  };
  return <Modal title="Create repository" onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label>Name<input required pattern="[a-z0-9-]+" value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })} placeholder="stable-aarch64" /></label>
    <label>Display name<input required value={draft.displayName} onChange={event => setDraft({ ...draft, displayName: event.target.value })} placeholder="Stable aarch64" /></label>
    <label className="span-2">Base path<input required value={draft.basePath} onChange={event => setDraft({ ...draft, basePath: event.target.value })} /></label>
    <label>Distribution<input required value={draft.distribution} onChange={event => setDraft({ ...draft, distribution: event.target.value })} /></label>
    <label>Architecture<select value={draft.arch} onChange={event => setDraft({ ...draft, arch: event.target.value })}><option>aarch64</option><option>x86_64</option></select></label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>Cancel</button><button className="button button--primary"><Boxes /> Create repository</button></footer>
  </form></Modal>;
}
