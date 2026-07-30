import { Download, FileArchive, GitBranch, Pencil, Plus, RefreshCw, Trash2 } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import type { SourcePackage } from "../types";

export function Sources() {
  const state = useAsync(api.sources, []);
  const [editing, setEditing] = useState<SourcePackage | "new" | null>(null);
  const [message, setMessage] = useState("");
  const packages = state.data?.packages ?? [];
  const fetchSource = async (name: string) => { await api.fetchSource(name); setMessage(`Fetch queued for ${name}.`); state.reload(); };
  const disable = async (source: SourcePackage) => {
    if (!window.confirm(`Disable ${source.packageName}?`)) return;
    await api.disableSource(source.packageName, source.revision); setMessage(`${source.packageName} disabled.`); state.reload();
  };
  return <>
    <PageHeader eyebrow="Source provenance" title="Sources" description="Pin, revision, and fetch package inputs through a durable source catalog." actions={<><button className="button button--primary" onClick={() => setEditing("new")}><Plus /> Add source</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> Refresh</button></>} />
    {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
    {state.loading ? <Loading label="Loading source catalog" /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : packages.length ? <div className="source-list">{packages.map(source => <article className="source-card" key={source.packageId}>
      <span className="source-card__icon">{source.sourceType === "Git" ? <GitBranch /> : <FileArchive />}</span>
      <div className="source-card__main"><div><h2>{source.packageName}</h2><span className="revision">r{source.revision}</span><Status value={source.status} /></div><p title={source.sourceUrl}>{source.sourceUrl}</p><small>{source.sourceBranch || "SHA-256 pinned"} · {source.lastFetchedAt ? `Fetched ${dateTime(source.lastFetchedAt)}` : "Never fetched"}</small></div>
      <div className="source-card__actions"><button className="button button--secondary button--small" onClick={() => void fetchSource(source.packageName)}><Download /> Fetch</button><button className="icon-button" onClick={() => setEditing(source)} aria-label={`Edit ${source.packageName}`}><Pencil /></button><button className="icon-button danger" onClick={() => void disable(source)} aria-label={`Disable ${source.packageName}`}><Trash2 /></button></div>
    </article>)}</div> : <Empty title="No package sources yet" detail="Add a package once; Lumina will validate, revision, and fetch it automatically." action={<button className="button button--primary" onClick={() => setEditing("new")}><Plus /> Add first source</button>} />}
    {editing && <SourceModal source={editing === "new" ? undefined : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); setMessage("Source saved and queued for fetch."); state.reload(); }} />}
  </>;
}

function SourceModal({ source, onClose, onSaved }: { source?: SourcePackage; onClose: () => void; onSaved: () => void }) {
  const [draft, setDraft] = useState({
    slug: source?.packageName ?? "", sourceUrl: source?.sourceUrl ?? "", sourceType: source?.sourceType ?? "Git",
    sourceReference: source?.sourceBranch ?? "main", expectedSha256: source?.expectedSha256 ?? "", specPath: source?.specPath ?? "",
    buildImage: source?.buildImage ?? "", isEnabled: source?.isEnabled ?? true, fetchAutomatically: true, expectedRevision: source?.revision
  });
  const [error, setError] = useState("");
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError("");
    try { await api.saveSource(source?.packageName, draft); onSaved(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Unable to save source."); }
  };
  return <Modal title={source ? `Edit ${source.packageName}` : "Add package source"} onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label>Package slug<input required pattern="[a-z0-9][a-z0-9+.-]*" disabled={Boolean(source)} value={draft.slug} onChange={event => setDraft({ ...draft, slug: event.target.value })} placeholder="my-package" /></label>
    <label>Source type<select value={draft.sourceType} onChange={event => setDraft({ ...draft, sourceType: event.target.value })}><option>Git</option><option>Tar</option><option>Http</option></select></label>
    <label className="span-2">HTTPS source URL<input required type="url" value={draft.sourceUrl} onChange={event => setDraft({ ...draft, sourceUrl: event.target.value })} placeholder="https://github.com/org/project.git" /></label>
    {draft.sourceType === "Git" ? <label>Branch, tag, or commit<input required value={draft.sourceReference} onChange={event => setDraft({ ...draft, sourceReference: event.target.value })} /></label> : <label className="span-2">Expected SHA-256<input required minLength={64} maxLength={64} value={draft.expectedSha256} onChange={event => setDraft({ ...draft, expectedSha256: event.target.value })} /></label>}
    <label>Spec path<input value={draft.specPath} onChange={event => setDraft({ ...draft, specPath: event.target.value })} placeholder="packaging/app.spec" /></label>
    <label>Build image override<input value={draft.buildImage} onChange={event => setDraft({ ...draft, buildImage: event.target.value })} /></label>
    <label className="checkbox span-2"><input type="checkbox" checked={draft.fetchAutomatically} onChange={event => setDraft({ ...draft, fetchAutomatically: event.target.checked })} /> Fetch automatically after saving</label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>Cancel</button><button className="button button--primary"><Download /> Save and fetch</button></footer>
  </form></Modal>;
}
