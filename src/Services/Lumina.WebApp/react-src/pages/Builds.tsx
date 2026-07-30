import { Ban, ChevronLeft, ChevronRight, Filter, Hammer, RefreshCw, Trash2 } from "lucide-react";
import { useState } from "react";
import { Link } from "../lib/router";
import { Empty, ErrorState, Loading, Notice, PageHeader, Status } from "../components/ui";
import { api } from "../lib/api";
import { shortId, timeAgo } from "../lib/format";
import { useAsync } from "../lib/useAsync";

const filters = ["", "Queued", "Building", "Success", "Failed", "Cancelled"];

export function Builds() {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState("");
  const [message, setMessage] = useState("");
  const state = useAsync(() => api.builds(page, status), [page, status]);
  const builds = state.data?.builds ?? [];
  const pages = Math.max(1, Math.ceil((state.data?.totalCount ?? 0) / 20));

  const clearQueue = async () => {
    if (!window.confirm("Remove all queued builds? Running builds will not be affected.")) return;
    await api.clearBuildQueue();
    setMessage("Queued builds cleared.");
    state.reload();
  };

  return (
    <>
      <PageHeader eyebrow="Execution history" title="Builds" description="Inspect every package build, from queued work to verified artifacts." actions={<><button className="button button--danger-ghost" onClick={() => void clearQueue()}><Trash2 /> Clear queue</button><button className="button button--secondary" onClick={state.reload}><RefreshCw /> Refresh</button></>} />
      {message && <Notice kind="success" onClose={() => setMessage("")}>{message}</Notice>}
      <div className="toolbar"><div className="filter-group" aria-label="Filter builds"><Filter />{filters.map(item => <button key={item || "all"} className={status === item ? "active" : ""} onClick={() => { setStatus(item); setPage(1); }}>{item || "All"}</button>)}</div></div>
      {state.loading ? <Loading label="Loading build history" /> : state.error ? <ErrorState error={state.error} retry={state.reload} /> : builds.length ? (
        <div className="panel table-panel">
          <div className="table-wrap"><table><caption className="sr-only">Build history</caption><thead><tr><th>Build</th><th>Status</th><th>Pipeline</th><th>Triggered by</th><th>Created</th><th aria-label="Actions" /></tr></thead>
            <tbody>{builds.map(build => <tr key={build.id}><th scope="row"><Link to={`/builds/${build.id}`}><span className="table-title">{build.specName}</span><code>{shortId(build.id)}</code></Link></th><td><Status value={build.status} domain="build" /></td><td><code>{shortId(build.pipelineId)}</code></td><td>{build.triggeredBy}</td><td title={build.createdAt}>{timeAgo(build.createdAt)}</td><td><Link className="icon-button" to={`/builds/${build.id}`} aria-label={`Inspect ${build.specName}`}><ChevronRight /></Link></td></tr>)}</tbody>
          </table></div>
          <footer className="pagination"><span>{state.data?.totalCount} builds · page {page} of {pages}</span><div><button className="icon-button" disabled={page === 1} onClick={() => setPage(value => value - 1)}><ChevronLeft /></button><button className="icon-button" disabled={page === pages} onClick={() => setPage(value => value + 1)}><ChevronRight /></button></div></footer>
        </div>
      ) : <Empty title="No matching builds" detail="There are no build executions in this state." action={<Link className="button button--primary" to="/pipelines"><Hammer /> Run a pipeline</Link>} />}
    </>
  );
}
