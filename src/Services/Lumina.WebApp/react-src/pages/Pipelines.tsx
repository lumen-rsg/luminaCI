import { Box, GitBranch, MoreHorizontal, Pencil, Play, Plus, RefreshCw, Search, Trash2, X } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime, hostName } from "../lib/format";
import { useNavigate } from "../lib/router";
import { useAsync } from "../lib/useAsync";
import type { Pipeline } from "../types";

const defaultDraft = {
  name: "", description: "", gitRepoUrl: "", gitBranch: "main", specPath: "",
  buildImage: "", targetDistribution: "fedora", targetRelease: "44", targetArchitecture: "aarch64"
};

export function Pipelines() {
  const navigate = useNavigate();
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [editing, setEditing] = useState<Pipeline | "new" | null>(null);
  const [message, setMessage] = useState("");
  const state = useAsync(() => api.pipelines(1, query), [query]);
  const pipelines = state.data?.pipelines ?? [];

  const trigger = async (pipeline: Pipeline) => {
    try {
      const build = await api.triggerPipeline(pipeline.id);
      navigate(`/builds/${build.id}`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to start build.");
    }
  };
  const remove = async (pipeline: Pipeline) => {
    if (!window.confirm(`Delete pipeline “${pipeline.name}”? This cannot be undone.`)) return;
    await api.deletePipeline(pipeline.id);
    setMessage(`${pipeline.name} deleted.`);
    state.reload();
  };

  return <>
    <PageHeader eyebrow="Build definitions" title="Pipelines" description="Create repeatable delivery paths from source to signed package." actions={<><button className="button button--primary" onClick={() => setEditing("new")}><Plus /> New pipeline</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> Refresh</button></>} />
    {message && <Notice kind={message.includes("Unable") ? "danger" : "success"} onClose={() => setMessage("")}>{message}</Notice>}
    <form className="searchbar" onSubmit={event => { event.preventDefault(); setQuery(search.trim()); }}><Search /><input value={search} onChange={event => setSearch(event.target.value)} placeholder="Search pipelines by name…" aria-label="Search pipelines" />{search && <button type="button" onClick={() => { setSearch(""); setQuery(""); }} aria-label="Clear search"><X /></button>}<button className="button button--secondary">Search</button></form>
    {state.loading ? <Loading label="Loading pipeline definitions" /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : pipelines.length ? <div className="pipeline-grid">{pipelines.map(pipeline => <article className="pipeline-card" key={pipeline.id}>
      <header><span className="pipeline-card__icon"><GitBranch /></span><Status value={pipeline.status} /><button className="icon-button" aria-label={`More actions for ${pipeline.name}`}><MoreHorizontal /></button></header>
      <h2>{pipeline.name}</h2><p>{pipeline.description || "No description provided."}</p>
      <div className="pipeline-source"><GitBranch /><span><small>Source</small><strong>{hostName(pipeline.gitRepoUrl)}</strong></span><code>{pipeline.gitBranch || "—"}</code></div>
      <dl><div><dt>Stages</dt><dd>{pipeline.stepCount}</dd></div><div><dt>Owner</dt><dd>{pipeline.createdBy}</dd></div><div><dt>Created</dt><dd>{dateTime(pipeline.createdAt)}</dd></div></dl>
      <footer><button className="button button--primary" onClick={() => void trigger(pipeline)}><Play /> Run build</button><button className="button button--secondary" onClick={() => setEditing(pipeline)}><Pencil /> Edit</button><button className="icon-button danger" onClick={() => void remove(pipeline)} aria-label={`Delete ${pipeline.name}`}><Trash2 /></button></footer>
    </article>)}</div> : <Empty title="No pipelines found" detail={query ? "Try a different search term." : "Create a pipeline to define your first repeatable build."} action={!query && <button className="button button--primary" onClick={() => setEditing("new")}><Plus /> Create pipeline</button>} />}
    {editing && <PipelineModal pipeline={editing === "new" ? undefined : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); setMessage("Pipeline saved."); state.reload(); }} />}
  </>;
}

function PipelineModal({ pipeline, onClose, onSaved }: { pipeline?: Pipeline; onClose: () => void; onSaved: () => void }) {
  const [draft, setDraft] = useState({ ...defaultDraft, ...pipeline });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const update = (field: string, value: string) => setDraft(valueBefore => ({ ...valueBefore, [field]: value }));
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true); setError("");
    const payload = {
      ...draft,
      steps: pipeline?.steps?.map(({ type, name, order, configuration }) => ({ type, name, order, configuration })) ?? [
        { type: "Build", name: "Build package", order: 1, configuration: {} },
        { type: "Sign", name: "Sign artifact", order: 2, configuration: {} },
        { type: "Scan", name: "Security scan", order: 3, configuration: {} }
      ],
      tags: pipeline?.tags ?? [], buildProfile: `${draft.targetDistribution}-${draft.targetRelease}-${draft.targetArchitecture}`,
      expectedUpdatedAt: pipeline?.updatedAt
    };
    try {
      if (pipeline) await api.updatePipeline(pipeline.id, payload); else await api.createPipeline(payload);
      onSaved();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Unable to save pipeline.");
    } finally { setBusy(false); }
  };
  return <Modal title={pipeline ? `Edit ${pipeline.name}` : "Create pipeline"} onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label className="span-2">Pipeline name<input required maxLength={120} value={draft.name} onChange={event => update("name", event.target.value)} placeholder="release-packages" /></label>
    <label className="span-2">Description<textarea rows={3} maxLength={500} value={draft.description} onChange={event => update("description", event.target.value)} placeholder="What this delivery pipeline produces" /></label>
    <label className="span-2">Git repository URL<input type="url" value={draft.gitRepoUrl || ""} onChange={event => update("gitRepoUrl", event.target.value)} placeholder="https://github.com/org/project.git" /></label>
    <label>Branch<input value={draft.gitBranch || ""} onChange={event => update("gitBranch", event.target.value)} /></label>
    <label>Spec path<input value={draft.specPath || ""} onChange={event => update("specPath", event.target.value)} placeholder="packaging/app.spec" /></label>
    <label>Distribution<input value={draft.targetDistribution || ""} onChange={event => update("targetDistribution", event.target.value)} /></label>
    <label>Release<input value={draft.targetRelease || ""} onChange={event => update("targetRelease", event.target.value)} /></label>
    <label>Architecture<select value={draft.targetArchitecture || ""} onChange={event => update("targetArchitecture", event.target.value)}><option>aarch64</option><option>x86_64</option></select></label>
    <label>Build image<input value={draft.buildImage || ""} onChange={event => update("buildImage", event.target.value)} placeholder="Default approved image" /></label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>Cancel</button><button className="button button--primary" disabled={busy}><Box /> {busy ? "Saving…" : "Save pipeline"}</button></footer>
  </form></Modal>;
}
