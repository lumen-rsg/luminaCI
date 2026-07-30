import { Archive, Boxes, Database, FolderSync, Plus, RefreshCw } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Empty, ErrorState, Loading, Modal, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import { usePreferences } from "../i18n/PreferencesContext";
import { useAutoRefresh } from "../lib/useAutoRefresh";

export function Repositories() {
  const { t, locale } = usePreferences();
  const state = useAsync(api.repositories, []);
  useAutoRefresh(state.reload);
  const [creating, setCreating] = useState(false);
  const [message, setMessage] = useState("");
  const repos = state.data?.repositories ?? [];
  const sync = async (id: string, name: string) => {
    await api.syncRepository(id); setMessage(t("repositories.syncStarted", { name })); state.reload();
  };
  return <>
    <PageHeader eyebrow={t("repositories.eyebrow")} title={t("repositories.title")} description={t("repositories.description")} actions={<><button className="button button--primary" onClick={() => setCreating(true)}><Plus /> {t("repositories.new")}</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> {t("common.refresh")}</button></>} />
    {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
    {state.loading ? <Loading label={t("repositories.loading")} /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : repos.length ? <div className="repo-grid">{repos.map(repo => <article className="repo-card" key={repo.id}>
      <header><span className="repo-card__icon"><Database /></span><Status value={repo.isActive ? "Active" : "Disabled"} /></header>
      <h2>{repo.displayName}</h2><code>{repo.name}</code>
      <div className="repo-metric"><strong>{repo.packageCount}</strong><span>{t("repositories.packages")}</span><Archive /></div>
      <dl><div><dt>{t("repositories.distribution")}</dt><dd>{repo.distribution}</dd></div><div><dt>{t("repositories.architecture")}</dt><dd>{repo.arch}</dd></div><div><dt>{t("common.created")}</dt><dd>{dateTime(repo.createdAt, locale)}</dd></div></dl>
      <footer><button className="button button--secondary" onClick={() => void sync(repo.id, repo.displayName)}><FolderSync /> {t("repositories.sync")}</button></footer>
    </article>)}</div> : <Empty title={t("repositories.none")} detail={t("repositories.noneDetail")} action={<button className="button button--primary" onClick={() => setCreating(true)}><Plus /> {t("repositories.create")}</button>} />}
    {creating && <RepositoryModal onClose={() => setCreating(false)} onSaved={() => { setCreating(false); setMessage(t("repositories.created")); state.reload(); }} />}
  </>;
}

function RepositoryModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { t } = usePreferences();
  const [draft, setDraft] = useState({ name: "", displayName: "", basePath: "/var/lib/lumina/repositories", arch: "aarch64", distribution: "fedora-44" });
  const [error, setError] = useState("");
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError("");
    try { await api.createRepository({ ...draft, createdBy: "web-console" }); onSaved(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : t("repositories.error")); }
  };
  return <Modal title={t("repositories.create")} onClose={onClose}><form className="form-grid" onSubmit={submit}>
    <label>{t("repositories.name")}<input required pattern="[a-z0-9-]+" value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })} placeholder="stable-aarch64" /></label>
    <label>{t("repositories.displayName")}<input required value={draft.displayName} onChange={event => setDraft({ ...draft, displayName: event.target.value })} placeholder="Stable aarch64" /></label>
    <label className="span-2">{t("repositories.basePath")}<input required value={draft.basePath} onChange={event => setDraft({ ...draft, basePath: event.target.value })} /></label>
    <label>{t("repositories.distribution")}<input required value={draft.distribution} onChange={event => setDraft({ ...draft, distribution: event.target.value })} /></label>
    <label>{t("repositories.architecture")}<select value={draft.arch} onChange={event => setDraft({ ...draft, arch: event.target.value })}><option>aarch64</option><option>x86_64</option></select></label>
    {error && <div className="form-error span-2" role="alert">{error}</div>}
    <footer className="form-actions span-2"><button type="button" className="button button--secondary" onClick={onClose}>{t("common.cancel")}</button><button className="button button--primary"><Boxes /> {t("repositories.create")}</button></footer>
  </form></Modal>;
}
