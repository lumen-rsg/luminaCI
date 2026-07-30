import { Ban, ChevronLeft, ChevronRight, Filter, Hammer, RefreshCw, Trash2 } from "lucide-react";
import { useState } from "react";
import { Link } from "../lib/router";
import { Empty, ErrorState, Loading, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { shortId, timeAgo } from "../lib/format";
import { usePreferences } from "../i18n/PreferencesContext";
import type { TranslationKey } from "../i18n/translations";
import { useAsync } from "../lib/useAsync";
import { useAutoRefresh } from "../lib/useAutoRefresh";

const filters = ["", "Queued", "Building", "Success", "Failed", "Cancelled"];

export function Builds() {
  const { t, locale } = usePreferences();
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState("");
  const [message, setMessage] = useState("");
  const state = useAsync(() => api.builds(page, status), [page, status]);
  useAutoRefresh(state.reload);
  const builds = state.data?.builds ?? [];
  const pages = Math.max(1, Math.ceil((state.data?.totalCount ?? 0) / 20));

  const clearQueue = async () => {
    if (!window.confirm(t("builds.clearConfirm"))) return;
    await api.clearBuildQueue();
    setMessage(t("builds.cleared"));
    state.reload();
  };

  return (
    <>
      <PageHeader eyebrow={t("builds.eyebrow")} title={t("builds.title")} description={t("builds.description")} actions={<><button className="button button--danger-ghost" onClick={() => void clearQueue()}><Trash2 /> {t("builds.clearQueue")}</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> {t("common.refresh")}</button></>} />
      {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
      <div className="toolbar"><div className="filter-group" aria-label={t("builds.filter")}><Filter />{filters.map(item => <button key={item || "all"} className={status === item ? "active" : ""} onClick={() => { setStatus(item); setPage(1); }}>{item ? t(`status.${item}` as TranslationKey) : t("builds.all")}</button>)}</div></div>
      {state.loading ? <Loading label={t("builds.loading")} /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : builds.length ? (
        <div className="panel table-panel">
          <div className="table-wrap"><table><caption className="sr-only">{t("builds.caption")}</caption><thead><tr><th>{t("builds.build")}</th><th>{t("common.status")}</th><th>{t("builds.pipeline")}</th><th>{t("builds.triggeredBy")}</th><th>{t("common.created")}</th><th aria-label={t("common.actions")} /></tr></thead>
            <tbody>{builds.map(build => <tr key={build.id}><th scope="row"><Link to={`/builds/${build.id}`}><span className="table-title">{build.specName}</span><code>{shortId(build.id)}</code></Link></th><td><Status value={build.status} domain="build" /></td><td><code>{shortId(build.pipelineId)}</code></td><td>{build.triggeredBy}</td><td title={build.createdAt}>{timeAgo(build.createdAt, locale)}</td><td><Link className="icon-button" to={`/builds/${build.id}`} aria-label={t("builds.inspect", { name: build.specName })}><ChevronRight /></Link></td></tr>)}</tbody>
          </table></div>
          <footer className="pagination"><span>{t("builds.page", { count: state.data?.totalCount ?? 0, page, pages })}</span><div><button className="icon-button" disabled={page === 1} onClick={() => setPage(value => value - 1)}><ChevronLeft /></button><button className="icon-button" disabled={page === pages} onClick={() => setPage(value => value + 1)}><ChevronRight /></button></div></footer>
        </div>
      ) : <Empty title={t("builds.none")} detail={t("builds.noneDetail")} action={<Link className="button button--primary" to="/pipelines"><Hammer /> {t("builds.runPipeline")}</Link>} />}
    </>
  );
}
