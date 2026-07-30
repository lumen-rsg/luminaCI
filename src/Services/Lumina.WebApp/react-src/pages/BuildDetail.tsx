import { ArrowLeft, Ban, Box, CheckCircle2, Clock3, Copy, Download, FileCode2, GitCommit, RefreshCw, ShieldCheck, Terminal, XCircle } from "lucide-react";
import { useState } from "react";
import { Link, useLocation } from "../lib/router";
import { Empty, ErrorState, Loading, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime, fileSize, isStatus, shortId, statusLabel } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import type { Build } from "../types";
import { usePreferences } from "../i18n/PreferencesContext";
import { useAutoRefresh } from "../lib/useAutoRefresh";

export function BuildDetail() {
  const { t } = usePreferences();
  const { pathname } = useLocation();
  const id = decodeURIComponent(pathname.slice("/builds/".length));
  const state = useAsync(() => Promise.all([api.build(id), api.buildLogs(id).catch(() => "")]).then(([build, logs]) => ({ ...build, logs: logs || build.logs })), [id]);
  const [message, setMessage] = useState("");
  const running = isStatus(state.data?.status, "Building", "build") || isStatus(state.data?.status, "Queued", "build");

  useAutoRefresh(state.reload, running);

  const cancel = async () => {
    if (!window.confirm(t("buildDetail.cancelConfirm"))) return;
    await api.cancelBuild(id);
    setMessage(t("buildDetail.cancelled"));
    state.reload();
  };

  return (
    <>
      <Link className="back-link" to="/builds"><ArrowLeft /> {t("buildDetail.back")}</Link>
      <PageHeader eyebrow={t("buildDetail.eyebrow")} title={t("buildDetail.title", { id: shortId(id) })} description={t("buildDetail.description")} actions={<>{running && <button className="button button--danger-ghost" onClick={() => void cancel()}><Ban /> {t("buildDetail.cancel")}</button>}<button className="button button--secondary" onClick={state.reload}><RefreshCw /> {t("common.refresh")}</button></>} />
      {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
      {state.loading && !state.data ? <Loading label={t("buildDetail.loading")} /> : state.error || !state.data ? <ErrorState error={state.error} retry={state.reload} /> : <BuildContent build={state.data} />}
    </>
  );
}

function BuildContent({ build }: { build: Build }) {
  const { t, locale } = usePreferences();
  const duration = build.startedAt ? Math.max(0, Math.round(((build.completedAt ? new Date(build.completedAt) : new Date()).getTime() - new Date(build.startedAt).getTime()) / 1000)) : null;
  const copyLogs = () => void navigator.clipboard.writeText(build.logs || "");
  return <div className="page-stack">
    <section className="build-hero">
      <div><span className="eyebrow">{t("buildDetail.current")}</span><Status value={build.status} domain="build" /><h2>{build.specName}</h2><p>{t("buildDetail.triggered")} <strong>{build.triggeredBy}</strong> · {dateTime(build.createdAt, locale)}</p></div>
      <dl><div><dt><Clock3 /> {t("buildDetail.duration")}</dt><dd>{duration === null ? t("buildDetail.notStarted") : t("buildDetail.durationValue", { minutes: Math.floor(duration / 60), seconds: duration % 60 })}</dd></div><div><dt><GitCommit /> {t("buildDetail.commit")}</dt><dd><code>{shortId(build.commitSha)}</code></dd></div><div><dt><Box /> {t("buildDetail.target")}</dt><dd>{[build.targetDistribution, build.targetRelease, build.targetArchitecture].filter(Boolean).join(" · ") || t("buildDetail.defaultProfile")}</dd></div></dl>
    </section>

    <section className="panel">
      <header className="panel__header"><div><span className="eyebrow">{t("buildDetail.graph")}</span><h2>{t("buildDetail.stages")}</h2></div></header>
      <ol className="stages">{build.stepRuns?.length ? [...build.stepRuns].sort((a, b) => a.order - b.order).map(step => <li key={step.id} className={`stage stage--${statusLabel(step.status, "step").toLowerCase()}`}><span className="stage__number">{isStatus(step.status, "Success", "step") ? <CheckCircle2 /> : isStatus(step.status, "Failed", "step") ? <XCircle /> : step.order}</span><div><strong>{step.name}</strong><small>{step.error || step.type}</small></div><Status value={step.status} domain="step" /></li>) : <li className="stage"><span className="stage__number">1</span><div><strong>{t("buildDetail.legacy")}</strong><small>{t("buildDetail.singleStage")}</small></div><Status value={build.status} domain="build" /></li>}</ol>
    </section>

    <section className="panel logs-panel">
      <header className="panel__header"><div><span className="eyebrow">{t("buildDetail.output")}</span><h2><Terminal /> {t("buildDetail.logs")}</h2></div><button className="button button--secondary button--small" onClick={copyLogs}><Copy /> {t("buildDetail.copy")}</button></header>
      <pre tabIndex={0}>{build.logs || t("buildDetail.waitingLogs")}</pre>
    </section>

    <section className="panel">
      <header className="panel__header"><div><span className="eyebrow">{t("buildDetail.supplyChain")}</span><h2>{t("buildDetail.artifacts")}</h2></div><ShieldCheck /></header>
      {build.artifacts?.length ? <div className="artifact-grid">{build.artifacts.map(artifact => <article key={artifact.id}><div className="artifact-icon"><FileCode2 /></div><div><h3>{artifact.fileName}</h3><p>{fileSize(artifact.fileSize)} · <code>{shortId(artifact.hashSha256)}</code></p><div><Status value={artifact.signingKeyFingerprint ? "Signed" : "Unsigned"} /><Status value={artifact.cveScanStatus} domain="scan" /></div></div><a className="icon-button" href={`/api/builds/${build.id}/artifacts/${encodeURIComponent(artifact.fileName)}`} aria-label={t("buildDetail.download", { name: artifact.fileName })}><Download /></a></article>)}</div> : <Empty title={t("buildDetail.noArtifacts")} detail={t("buildDetail.noArtifactsDetail")} />}
    </section>
  </div>;
}
