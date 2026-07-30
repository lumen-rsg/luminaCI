import {
  AlertTriangle, CheckCircle2, Inbox, LoaderCircle, RefreshCw, XCircle
} from "lucide-react";
import type { ReactNode } from "react";
import { tone } from "../lib/format";

export function PageHeader({ eyebrow, title, description, actions }: {
  eyebrow: string; title: string; description: string; actions?: ReactNode;
}) {
  return (
    <header className="page-header">
      <div>
        <span className="eyebrow">{eyebrow}</span>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </header>
  );
}

export function Status({ value }: { value?: string }) {
  return <span className={`status status--${tone(value)}`}><span />{value || "Unknown"}</span>;
}

export function Loading({ label = "Loading workspace" }: { label?: string }) {
  return <div className="state state--loading"><LoaderCircle className="spin" /><p>{label}</p></div>;
}

export function Empty({ title, detail, action }: { title: string; detail: string; action?: ReactNode }) {
  return <div className="state"><Inbox /><h3>{title}</h3><p>{detail}</p>{action}</div>;
}

export function ErrorState({ error, retry }: { error: unknown; retry?: () => void }) {
  const message = error instanceof Error ? error.message : "An unexpected error occurred.";
  return (
    <div className="state state--error" role="alert">
      <AlertTriangle /><h3>Couldn’t load this view</h3><p>{message}</p>
      {retry && <button className="button button--secondary" onClick={retry}><RefreshCw /> Try again</button>}
    </div>
  );
}

export function Notice({ kind, children, onClose }: {
  kind: "success" | "danger"; children: ReactNode; onClose?: () => void;
}) {
  const Icon = kind === "success" ? CheckCircle2 : XCircle;
  return <div className={`notice notice--${kind}`} role={kind === "danger" ? "alert" : "status"}><Icon />{children}{onClose && <button onClick={onClose} aria-label="Dismiss">×</button>}</div>;
}

export function StatCard({ label, value, detail, icon, tone: cardTone = "mint" }: {
  label: string; value: string | number; detail: string; icon: ReactNode; tone?: string;
}) {
  return (
    <article className={`stat-card stat-card--${cardTone}`}>
      <div className="stat-card__icon">{icon}</div>
      <div><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>
    </article>
  );
}

export function Modal({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={event => event.target === event.currentTarget && onClose()}>
      <section className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title">
        <header><div><span className="eyebrow">Configuration</span><h2 id="modal-title">{title}</h2></div><button className="icon-button" onClick={onClose} aria-label="Close">×</button></header>
        {children}
      </section>
    </div>
  );
}
