import { ArrowLeft, Ban, Box, CheckCircle2, Clock3, Copy, Download, FileCode2, GitCommit, RefreshCw, ShieldCheck, Terminal, XCircle } from "lucide-react";
import { useEffect, useState } from "react";
import { Link, useLocation } from "../lib/router";
import { Empty, ErrorState, Loading, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { dateTime, fileSize, isStatus, shortId, statusLabel } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import type { Build } from "../types";

export function BuildDetail() {
  const { pathname } = useLocation();
  const id = decodeURIComponent(pathname.slice("/builds/".length));
  const state = useAsync(() => Promise.all([api.build(id), api.buildLogs(id).catch(() => "")]).then(([build, logs]) => ({ ...build, logs: logs || build.logs })), [id]);
  const [message, setMessage] = useState("");
  const running = isStatus(state.data?.status, "Building", "build") || isStatus(state.data?.status, "Queued", "build");

  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(state.reload, 5000);
    return () => window.clearInterval(timer);
  }, [running, state.reload]);

  const cancel = async () => {
    if (!window.confirm("Cancel this build execution?")) return;
    await api.cancelBuild(id);
    setMessage("Cancellation requested.");
    state.reload();
  };

  return (
    <>
      <Link className="back-link" to="/builds"><ArrowLeft /> Build history</Link>
      <PageHeader eyebrow="Build execution" title={`Build ${shortId(id)}`} description="Live stages, provenance, logs, and artifact verification." actions={<>{running && <button className="button button--danger-ghost" onClick={() => void cancel()}><Ban /> Cancel build</button>}<button className="button button--secondary" onClick={state.reload}><RefreshCw /> Refresh</button></>} />
      {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
      {state.loading && !state.data ? <Loading label="Resolving build execution" /> : state.error || !state.data ? <ErrorState error={state.error} retry={state.reload} /> : <BuildContent build={state.data} />}
    </>
  );
}

function BuildContent({ build }: { build: Build }) {
  const duration = build.startedAt ? Math.max(0, Math.round(((build.completedAt ? new Date(build.completedAt) : new Date()).getTime() - new Date(build.startedAt).getTime()) / 1000)) : null;
  const copyLogs = () => void navigator.clipboard.writeText(build.logs || "");
  return <div className="page-stack">
    <section className="build-hero">
      <div><span className="eyebrow">Current state</span><Status value={build.status} domain="build" /><h2>{build.specName}</h2><p>Triggered by <strong>{build.triggeredBy}</strong> · {dateTime(build.createdAt)}</p></div>
      <dl><div><dt><Clock3 /> Duration</dt><dd>{duration === null ? "Not started" : `${Math.floor(duration / 60)}m ${duration % 60}s`}</dd></div><div><dt><GitCommit /> Commit</dt><dd><code>{shortId(build.commitSha)}</code></dd></div><div><dt><Box /> Target</dt><dd>{[build.targetDistribution, build.targetRelease, build.targetArchitecture].filter(Boolean).join(" · ") || "Default profile"}</dd></div></dl>
    </section>

    <section className="panel">
      <header className="panel__header"><div><span className="eyebrow">Execution graph</span><h2>Pipeline stages</h2></div></header>
      <ol className="stages">{build.stepRuns?.length ? [...build.stepRuns].sort((a, b) => a.order - b.order).map(step => <li key={step.id} className={`stage stage--${statusLabel(step.status, "step").toLowerCase()}`}><span className="stage__number">{isStatus(step.status, "Success", "step") ? <CheckCircle2 /> : isStatus(step.status, "Failed", "step") ? <XCircle /> : step.order}</span><div><strong>{step.name}</strong><small>{step.error || step.type}</small></div><Status value={step.status} domain="step" /></li>) : <li className="stage"><span className="stage__number">1</span><div><strong>Legacy build</strong><small>Single-stage execution</small></div><Status value={build.status} domain="build" /></li>}</ol>
    </section>

    <section className="panel logs-panel">
      <header className="panel__header"><div><span className="eyebrow">Live output</span><h2><Terminal /> Build logs</h2></div><button className="button button--secondary button--small" onClick={copyLogs}><Copy /> Copy</button></header>
      <pre tabIndex={0}>{build.logs || "Waiting for executor output…"}</pre>
    </section>

    <section className="panel">
      <header className="panel__header"><div><span className="eyebrow">Supply chain</span><h2>Artifacts</h2></div><ShieldCheck /></header>
      {build.artifacts?.length ? <div className="artifact-grid">{build.artifacts.map(artifact => <article key={artifact.id}><div className="artifact-icon"><FileCode2 /></div><div><h3>{artifact.fileName}</h3><p>{fileSize(artifact.fileSize)} · <code>{shortId(artifact.hashSha256)}</code></p><div><Status value={artifact.signingKeyFingerprint ? "Signed" : "Unsigned"} /><Status value={artifact.cveScanStatus} domain="scan" /></div></div><a className="icon-button" href={`/api/builds/${build.id}/artifacts/${encodeURIComponent(artifact.fileName)}`} aria-label={`Download ${artifact.fileName}`}><Download /></a></article>)}</div> : <Empty title="No artifacts yet" detail="Artifacts will appear after the build and verification stages complete." />}
    </section>
  </div>;
}
