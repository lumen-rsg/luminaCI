import { Activity, ArrowRight, CheckCircle2, Clock3, GitBranch, Hammer, RefreshCw, ShieldAlert, TimerReset, XCircle } from "lucide-react";
import { Link } from "../lib/router";
import { api } from "../lib/api";
import { shortId, timeAgo } from "../lib/format";
import { useAsync } from "../lib/useAsync";
import { Empty, ErrorState, Loading, PageHeader, StatCard, Status } from "../components/ui";

export function Dashboard() {
  const state = useAsync(async () => {
    const [stats, queue, builds, pipelines, scans] = await Promise.all([
      api.buildStats(), api.buildQueue(), api.builds(), api.pipelines(), api.scans()
    ]);
    return { stats, queue, builds: builds.builds ?? [], pipelines: pipelines.pipelines ?? [], scans: scans.scans ?? [] };
  }, []);

  return (
    <>
      <PageHeader eyebrow="Overview" title="Delivery pulse" description="Build health, executor activity, and release throughput at a glance." actions={<><Link className="button button--primary" to="/pipelines"><Hammer /> Start a build</Link><button className="button button--secondary" onClick={state.reload}><RefreshCw /> Refresh</button></>} />
      {state.loading ? <Loading /> : state.error || !state.data ? <ErrorState error={state.error} retry={state.reload} /> : <DashboardContent data={state.data} />}
    </>
  );
}

function DashboardContent({ data }: { data: NonNullable<ReturnType<typeof useAsync<never>>["data"]> | any }) {
  const successRate = data.stats.totalCount ? Math.round((data.stats.successfulCount / data.stats.totalCount) * 100) : 0;
  const active = data.queue.runningCount + data.queue.queuedCount;
  const vulnerabilities = data.scans.reduce((sum: number, scan: any) => sum + scan.criticalCount + scan.highCount, 0);
  return (
    <div className="page-stack">
      <section className="stats-grid" aria-label="Build statistics">
        <StatCard label="Total builds" value={data.stats.totalCount} detail="All recorded executions" icon={<Hammer />} />
        <StatCard label="Success rate" value={`${successRate}%`} detail={`${data.stats.successfulCount} successful`} icon={<CheckCircle2 />} tone="blue" />
        <StatCard label="Active work" value={active} detail={`${data.queue.queuedCount} waiting in queue`} icon={<TimerReset />} tone="amber" />
        <StatCard label="High risk findings" value={vulnerabilities} detail="Critical and high CVEs" icon={<ShieldAlert />} tone="red" />
      </section>

      <section className="dashboard-grid">
        <article className="panel panel--wide">
          <header className="panel__header"><div><span className="eyebrow">Execution stream</span><h2>Recent builds</h2></div><Link to="/builds">View all <ArrowRight /></Link></header>
          {data.builds.length ? <div className="activity-list">{data.builds.slice(0, 6).map((build: any) =>
            <Link to={`/builds/${build.id}`} className="activity-row" key={build.id}>
              <span className={`activity-icon activity-icon--${build.status.toLowerCase()}`}>{build.status === "Success" ? <CheckCircle2 /> : build.status === "Failed" ? <XCircle /> : <Activity />}</span>
              <span className="activity-main"><strong>{build.specName || `Build ${shortId(build.id)}`}</strong><small><code>{shortId(build.id)}</code> · {build.triggeredBy}</small></span>
              <Status value={build.status} /><time>{timeAgo(build.createdAt)}</time><ArrowRight className="row-arrow" />
            </Link>)}</div> : <Empty title="No builds yet" detail="Run a pipeline to begin tracking delivery activity." />}
        </article>

        <article className="panel">
          <header className="panel__header"><div><span className="eyebrow">Executors</span><h2>Build queue</h2></div><Clock3 /></header>
          <progress className="queue-meter" max="6" value={Math.min(6, active)} aria-label={`${active} active builds`} />
          <div className="queue-counts"><span><strong>{data.queue.runningCount}</strong>Running</span><span><strong>{data.queue.queuedCount}</strong>Queued</span></div>
          <div className="queue-list">{[...data.queue.running, ...data.queue.queued].slice(0, 4).map((build: any) => <Link key={build.id} to={`/builds/${build.id}`}><Activity /><span><strong>{build.specName}</strong><small>{shortId(build.id)}</small></span><Status value={build.status} /></Link>)}</div>
          {!active && <div className="quiet-state"><CheckCircle2 /> Executors are idle and ready.</div>}
        </article>
      </section>

      <section className="panel">
        <header className="panel__header"><div><span className="eyebrow">Definitions</span><h2>Pipeline inventory</h2></div><Link to="/pipelines">Manage pipelines <ArrowRight /></Link></header>
        <div className="pipeline-strip">{data.pipelines.slice(0, 4).map((pipeline: any) => <article key={pipeline.id}><div><GitBranch /><Status value={pipeline.status} /></div><h3>{pipeline.name}</h3><p>{pipeline.description || "No description provided."}</p><small>{pipeline.stepCount} steps · {pipeline.createdBy}</small></article>)}</div>
      </section>
    </div>
  );
}
