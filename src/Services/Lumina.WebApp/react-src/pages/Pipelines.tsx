import { Box, GitBranch, MoreHorizontal, Pencil, Play, Plus, RefreshCw, Search, Trash2, X } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime, hostName } from "../lib/format";
import { useNavigate } from "../lib/router";
import { useAsync } from "../lib/useAsync";
import type { Pipeline } from "../types";
import { usePreferences } from "../i18n/PreferencesContext";
import { useAutoRefresh } from "../lib/useAutoRefresh";

const defaultDraft = {
  name: "", description: "", gitRepoUrl: "", gitBranch: "main", specPath: "",
  triggerPaths: [] as string[],
  buildImage: "", targetDistribution: "fedora", targetRelease: "44", targetArchitecture: "aarch64",
  webhookSecret: `${crypto.randomUUID()}${crypto.randomUUID()}`
};

export function Pipelines() {
  const { t, locale } = usePreferences();
  const navigate = useNavigate();
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [editing, setEditing] = useState<Pipeline | "new" | null>(null);
  const [message, setMessage] = useState("");
  const state = useAsync(() => api.pipelines(1, query), [query]);
  useAutoRefresh(state.reload);
  const pipelines = state.data?.pipelines ?? [];

  const trigger = async (pipeline: Pipeline) => {
    try {
      const build = await api.triggerPipeline(pipeline.id);
      navigate(`/builds/${build.id}`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : t("pipelines.startError"));
    }
  };
  const remove = async (pipeline: Pipeline) => {
    if (!window.confirm(t("pipelines.deleteConfirm", { name: pipeline.name }))) return;
    await api.deletePipeline(pipeline.id);
    setMessage(t("pipelines.deleted", { name: pipeline.name }));
    state.reload();
  };

  return <>
    <PageHeader eyebrow={t("pipelines.eyebrow")} title={t("pipelines.title")} description={t("pipelines.description")} actions={<><button className="button button--primary" onClick={() => setEditing("new")}><Plus /> {t("pipelines.new")}</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> {t("common.refresh")}</button></>} />
    {message && <Notice kind={message === t("pipelines.startError") || message.toLowerCase().includes("unable") ? "danger" : "success"} onClose={() => setMessage("")}>{message}</Notice>}
    <form className="searchbar" onSubmit={event => { event.preventDefault(); setQuery(search.trim()); }}><Search /><input value={search} onChange={event => setSearch(event.target.value)} placeholder={t("pipelines.searchPlaceholder")} aria-label={t("common.search")} />{search && <button type="button" onClick={() => { setSearch(""); setQuery(""); }} aria-label={t("pipelines.clearSearch")}><X /></button>}<button className="button button--secondary">{t("common.search")}</button></form>
    {state.loading ? <Loading label={t("pipelines.loading")} /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : pipelines.length ? <div className="pipeline-grid">{pipelines.map(pipeline => <article className="pipeline-card" key={pipeline.id}>
      <header><span className="pipeline-card__icon"><GitBranch /></span><Status value={pipeline.status} domain="pipeline" /><button className="icon-button" aria-label={t("pipelines.more", { name: pipeline.name })}><MoreHorizontal /></button></header>
      <h2>{pipeline.name}</h2><p>{pipeline.description || t("common.noDescription")}</p>
      <div className="pipeline-source"><GitBranch /><span><small>{t("pipelines.source")}</small><strong>{pipeline.gitRepoUrl ? hostName(pipeline.gitRepoUrl) : t("pipelines.manualSpec")}</strong></span><code>{pipeline.gitBranch || "—"}</code></div>
      <dl><div><dt>{t("pipelines.stages")}</dt><dd>{pipeline.stepCount}</dd></div><div><dt>{t("pipelines.owner")}</dt><dd>{pipeline.createdBy}</dd></div><div><dt>{t("common.created")}</dt><dd>{dateTime(pipeline.createdAt, locale)}</dd></div></dl>
      <footer><button className="button button--primary" onClick={() => void trigger(pipeline)}><Play /> {t("pipelines.run")}</button><button className="button button--secondary" onClick={() => setEditing(pipeline)}><Pencil /> {t("pipelines.edit")}</button><button className="icon-button danger" onClick={() => void remove(pipeline)} aria-label={t("pipelines.delete", { name: pipeline.name })}><Trash2 /></button></footer>
    </article>)}</div> : <Empty title={t("pipelines.none")} detail={query ? t("pipelines.noneSearch") : t("pipelines.noneDetail")} action={!query && <button className="button button--primary" onClick={() => setEditing("new")}><Plus /> {t("pipelines.create")}</button>} />}
    {editing && <PipelineModal pipeline={editing === "new" ? undefined : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); setMessage(t("pipelines.saved")); state.reload(); }} />}
  </>;
}

function PipelineModal({ pipeline, onClose, onSaved }: { pipeline?: Pipeline; onClose: () => void; onSaved: () => void }) {
  const { t } = usePreferences();
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
        { type: 0, name: "Build package", order: 1, configuration: {} },
        { type: 2, name: "Security scan", order: 2, configuration: {} },
        { type: 1, name: "Sign artifact", order: 3, configuration: {} }
      ],
      tags: pipeline?.tags ?? [], buildProfile: `${draft.targetDistribution}-${draft.targetRelease}-${draft.targetArchitecture}`,
      expectedUpdatedAt: pipeline?.updatedAt
    };
    try {
      if (pipeline) await api.updatePipeline(pipeline.id, payload); else await api.createPipeline(payload);
      onSaved();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("pipelines.saveError"));
    } finally { setBusy(false); }
  };
  return <Modal title={pipeline ? t("pipelines.editTitle", { name: pipeline.name }) : t("pipelines.create")} onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label className="span-2">{t("pipelines.name")}<input required maxLength={120} value={draft.name} onChange={event => update("name", event.target.value)} placeholder={t("pipelines.namePlaceholder")} /></label>
    <label className="span-2">{t("pipelines.formDescription")}<textarea rows={3} maxLength={500} value={draft.description} onChange={event => update("description", event.target.value)} placeholder={t("pipelines.descriptionPlaceholder")} /></label>
    <label className="span-2">{t("pipelines.gitUrl")}<input type="url" value={draft.gitRepoUrl || ""} onChange={event => update("gitRepoUrl", event.target.value)} placeholder="https://github.com/org/project.git" /></label>
    {!pipeline && <label className="span-2">{t("pipelines.webhookSecret")}<input required minLength={16} value={draft.webhookSecret} onChange={event => update("webhookSecret", event.target.value)} autoComplete="new-password" /><small>{t("pipelines.webhookHint")}</small></label>}
    <label>{t("pipelines.branch")}<input value={draft.gitBranch || ""} onChange={event => update("gitBranch", event.target.value)} /></label>
    <label>{t("pipelines.specPath")}<input value={draft.specPath || ""} onChange={event => update("specPath", event.target.value)} placeholder="packaging/app.spec" /></label>
    <label className="span-2">{t("pipelines.triggerPaths")}<input value={(draft.triggerPaths ?? []).join(", ")} onChange={event => setDraft(valueBefore => ({ ...valueBefore, triggerPaths: event.target.value.split(",").map(value => value.trim()).filter(Boolean) }))} placeholder="packaging/app, shared/rpm" /><small>{t("pipelines.triggerPathsHint")}</small></label>
    <label>{t("pipelines.distribution")}<input value={draft.targetDistribution || ""} onChange={event => update("targetDistribution", event.target.value)} /></label>
    <label>{t("pipelines.release")}<input value={draft.targetRelease || ""} onChange={event => update("targetRelease", event.target.value)} /></label>
    <label>{t("pipelines.architecture")}<select value={draft.targetArchitecture || ""} onChange={event => update("targetArchitecture", event.target.value)}><option>aarch64</option><option>x86_64</option></select></label>
    <label>{t("pipelines.buildImage")}<input value={draft.buildImage || ""} onChange={event => update("buildImage", event.target.value)} placeholder={t("pipelines.defaultImage")} /></label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>{t("common.cancel")}</button><button className="button button--primary" disabled={busy}><Box /> {busy ? t("pipelines.saving") : t("pipelines.save")}</button></footer>
  </form></Modal>;
}
