import { Activity, ArrowRight, CheckCircle2, Clock3, GitBranch, Hammer, RefreshCw, ShieldAlert, TimerReset, XCircle } from "lucide-react";
import { Link } from "../lib/router";
import { api } from "../lib/api";
import { isStatus, shortId, statusLabel, timeAgo } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import { Empty, ErrorState, Loading, PageHeader, StatCard, Status } from "../components/ui";
import { usePreferences } from "../i18n/PreferencesContext";
import { useAutoRefresh } from "../lib/useAutoRefresh";

export function Dashboard() {
  const { t } = usePreferences();
  const state = useAsync(async () => {
    const [stats, queue, builds, pipelines, scans] = await Promise.all([
      api.buildStats(), api.buildQueue(), api.builds(), api.pipelines(), api.scans()
    ]);
    return { stats, queue, builds: builds.builds ?? [], pipelines: pipelines.pipelines ?? [], scans: scans.scans ?? [] };
  }, []);
  useAutoRefresh(state.reload);

  return (
    <>
      <PageHeader eyebrow={t("dashboard.eyebrow")} title={t("dashboard.title")} description={t("dashboard.description")} actions={<><Link className="button button--primary" to="/pipelines"><Hammer /> {t("dashboard.startBuild")}</Link><button className="button button--secondary" onClick={state.reload}><RefreshCw /> {t("common.refresh")}</button></>} />
      {state.loading ? <Loading /> : state.error || !state.data ? <ErrorState error={state.error} retry={state.reload} /> : <DashboardContent data={state.data} />}
    </>
  );
}

function DashboardContent({ data }: { data: NonNullable<ReturnType<typeof useAsync<never>>["data"]> | any }) {
  const { t, locale } = usePreferences();
  const successRate = data.stats.totalCount ? Math.round((data.stats.successfulCount / data.stats.totalCount) * 100) : 0;
  const active = data.queue.runningCount + data.queue.queuedCount;
  const vulnerabilities = data.scans.reduce((sum: number, scan: any) => sum + scan.criticalCount + scan.highCount, 0);
  return (
    <div className="page-stack">
      <section className="stats-grid" aria-label={t("dashboard.statsLabel")}>
        <StatCard label={t("dashboard.totalBuilds")} value={data.stats.totalCount} detail={t("dashboard.totalDetail")} icon={<Hammer />} />
        <StatCard label={t("dashboard.successRate")} value={`${successRate}%`} detail={t("dashboard.successful", { count: data.stats.successfulCount })} icon={<CheckCircle2 />} tone="blue" />
        <StatCard label={t("dashboard.activeWork")} value={active} detail={t("dashboard.waiting", { count: data.queue.queuedCount })} icon={<TimerReset />} tone="amber" />
        <StatCard label={t("dashboard.risk")} value={vulnerabilities} detail={t("dashboard.riskDetail")} icon={<ShieldAlert />} tone="red" />
      </section>

      <section className="dashboard-grid">
        <article className="panel panel--wide">
          <header className="panel__header"><div><span className="eyebrow">{t("dashboard.executionStream")}</span><h2>{t("dashboard.recentBuilds")}</h2></div><Link to="/builds">{t("dashboard.viewAll")} <ArrowRight /></Link></header>
          {data.builds.length ? <div className="activity-list">{data.builds.slice(0, 6).map((build: any) =>
            <Link to={`/builds/${build.id}`} className="activity-row" key={build.id}>
              <span className={`activity-icon activity-icon--${statusLabel(build.status, "build").toLowerCase()}`}>{isStatus(build.status, "Success", "build") ? <CheckCircle2 /> : isStatus(build.status, "Failed", "build") ? <XCircle /> : <Activity />}</span>
              <span className="activity-main"><strong>{build.specName || `Build ${shortId(build.id)}`}</strong><small><code>{shortId(build.id)}</code> · {build.triggeredBy}</small></span>
              <Status value={build.status} domain="build" /><time>{timeAgo(build.createdAt, locale)}</time><ArrowRight className="row-arrow" />
            </Link>)}</div> : <Empty title={t("dashboard.noBuilds")} detail={t("dashboard.noBuildsDetail")} />}
        </article>

        <article className="panel">
          <header className="panel__header"><div><span className="eyebrow">{t("dashboard.executors")}</span><h2>{t("dashboard.queue")}</h2></div><Clock3 /></header>
          <progress className="queue-meter" max="6" value={Math.min(6, active)} aria-label={t("dashboard.activeBuilds", { count: active })} />
          <div className="queue-counts"><span><strong>{data.queue.runningCount}</strong>{t("dashboard.running")}</span><span><strong>{data.queue.queuedCount}</strong>{t("dashboard.queued")}</span></div>
          <div className="queue-list">{[...data.queue.running, ...data.queue.queued].slice(0, 4).map((build: any) => <Link key={build.id} to={`/builds/${build.id}`}><Activity /><span><strong>{build.specName}</strong><small>{shortId(build.id)}</small></span><Status value={build.status} domain="build" /></Link>)}</div>
          {!active && <div className="quiet-state"><CheckCircle2 /> {t("dashboard.idle")}</div>}
        </article>
      </section>

      <section className="panel">
        <header className="panel__header"><div><span className="eyebrow">{t("dashboard.definitions")}</span><h2>{t("dashboard.inventory")}</h2></div><Link to="/pipelines">{t("dashboard.manage")} <ArrowRight /></Link></header>
        <div className="pipeline-strip">{data.pipelines.slice(0, 4).map((pipeline: any) => <article key={pipeline.id}><div><GitBranch /><Status value={pipeline.status} domain="pipeline" /></div><h3>{pipeline.name}</h3><p>{pipeline.description || t("common.noDescription")}</p><small>{t("dashboard.steps", { count: pipeline.stepCount })} · {pipeline.createdBy}</small></article>)}</div>
      </section>
    </div>
  );
}
