import { Download, FileArchive, GitBranch, Pencil, Plus, RefreshCw, Trash2 } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import type { SourcePackage } from "../types";
import { usePreferences } from "../i18n/PreferencesContext";
import { useAutoRefresh } from "../lib/useAutoRefresh";

export function Sources() {
  const { t, locale } = usePreferences();
  const state = useAsync(api.sources, []);
  useAutoRefresh(state.reload);
  const [editing, setEditing] = useState<SourcePackage | "new" | null>(null);
  const [message, setMessage] = useState("");
  const packages = state.data?.packages ?? [];
  const fetchSource = async (name: string) => { await api.fetchSource(name); setMessage(t("sources.fetchQueued", { name })); state.reload(); };
  const disable = async (source: SourcePackage) => {
    if (!window.confirm(t("sources.disableConfirm", { name: source.packageName }))) return;
    await api.disableSource(source.packageName, source.revision); setMessage(t("sources.disabled", { name: source.packageName })); state.reload();
  };
  return <>
    <PageHeader eyebrow={t("sources.eyebrow")} title={t("sources.title")} description={t("sources.description")} actions={<><button className="button button--primary" onClick={() => setEditing("new")}><Plus /> {t("sources.add")}</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> {t("common.refresh")}</button></>} />
    {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
    {state.loading ? <Loading label={t("sources.loading")} /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : packages.length ? <div className="source-list">{packages.map(source => <article className="source-card" key={source.packageId}>
      <span className="source-card__icon">{source.sourceType === "Git" || source.sourceType === 0 ? <GitBranch /> : <FileArchive />}</span>
      <div className="source-card__main"><div><h2>{source.packageName}</h2><span className="revision">r{source.revision}</span><Status value={source.status} domain="source" /></div><p title={source.sourceUrl}>{source.sourceUrl}</p><small>{source.sourceBranch || t("sources.pinned")} · {source.lastFetchedAt ? t("sources.fetched", { date: dateTime(source.lastFetchedAt, locale) }) : t("sources.neverFetched")}</small></div>
      <div className="source-card__actions"><button className="button button--secondary button--small" onClick={() => void fetchSource(source.packageName)}><Download /> {t("sources.fetch")}</button><button className="icon-button" onClick={() => setEditing(source)} aria-label={t("sources.edit", { name: source.packageName })}><Pencil /></button><button className="icon-button danger" onClick={() => void disable(source)} aria-label={t("sources.disable", { name: source.packageName })}><Trash2 /></button></div>
    </article>)}</div> : <Empty title={t("sources.none")} detail={t("sources.noneDetail")} action={<button className="button button--primary" onClick={() => setEditing("new")}><Plus /> {t("sources.addFirst")}</button>} />}
    {editing && <SourceModal source={editing === "new" ? undefined : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); setMessage(t("sources.saved")); state.reload(); }} />}
  </>;
}

function SourceModal({ source, onClose, onSaved }: { source?: SourcePackage; onClose: () => void; onSaved: () => void }) {
  const { t } = usePreferences();
  const [draft, setDraft] = useState({
    slug: source?.packageName ?? "", sourceUrl: source?.sourceUrl ?? "", sourceType: source?.sourceType === 1 ? "1" : source?.sourceType === 2 ? "2" : "0",
    sourceReference: source?.sourceBranch ?? "main", expectedSha256: source?.expectedSha256 ?? "", specPath: source?.specPath ?? "",
    buildImage: source?.buildImage ?? "", isEnabled: source?.isEnabled ?? true, fetchAutomatically: true, expectedRevision: source?.revision
  });
  const [error, setError] = useState("");
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError("");
    try { await api.saveSource(source?.packageName, { ...draft, sourceType: Number(draft.sourceType) }); onSaved(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : t("sources.error")); }
  };
  return <Modal title={source ? t("sources.edit", { name: source.packageName }) : t("sources.addPackage")} onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label>{t("sources.packageSlug")}<input required pattern="[a-z0-9][a-z0-9+.-]*" disabled={Boolean(source)} value={draft.slug} onChange={event => setDraft({ ...draft, slug: event.target.value })} placeholder="my-package" /></label>
    <label>{t("sources.sourceType")}<select value={draft.sourceType} onChange={event => setDraft({ ...draft, sourceType: event.target.value })}><option value="0">Git</option><option value="1">Tar</option><option value="2">HTTP</option></select></label>
    <label className="span-2">{t("sources.sourceUrl")}<input required type="url" value={draft.sourceUrl} onChange={event => setDraft({ ...draft, sourceUrl: event.target.value })} placeholder="https://github.com/org/project.git" /></label>
    {draft.sourceType === "0" ? <label>{t("sources.reference")}<input required value={draft.sourceReference} onChange={event => setDraft({ ...draft, sourceReference: event.target.value })} /></label> : <label className="span-2">{t("sources.sha")}<input required minLength={64} maxLength={64} value={draft.expectedSha256} onChange={event => setDraft({ ...draft, expectedSha256: event.target.value })} /></label>}
    <label>{t("sources.specPath")}<input value={draft.specPath} onChange={event => setDraft({ ...draft, specPath: event.target.value })} placeholder="packaging/app.spec" /></label>
    <label>{t("sources.imageOverride")}<input value={draft.buildImage} onChange={event => setDraft({ ...draft, buildImage: event.target.value })} /></label>
    <label className="checkbox span-2"><input type="checkbox" checked={draft.fetchAutomatically} onChange={event => setDraft({ ...draft, fetchAutomatically: event.target.checked })} /> {t("sources.fetchAutomatically")}</label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>{t("common.cancel")}</button><button className="button button--primary"><Download /> {t("sources.saveFetch")}</button></footer>
  </form></Modal>;
}
