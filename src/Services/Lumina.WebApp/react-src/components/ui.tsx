import {
  AlertTriangle, CheckCircle2, Inbox, LoaderCircle, RefreshCw, XCircle
} from "lucide-react";
import type { ReactNode } from "react";
import { usePreferences } from "../i18n/PreferencesContext";
import { en, type TranslationKey } from "../i18n/translations";
import { statusLabel, tone, type StatusDomain } from "../lib/format";

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

export function Status({ value, domain }: { value?: string | number; domain?: StatusDomain }) {
  const { t } = usePreferences();
  const label = statusLabel(value, domain);
  const key = `status.${label}` as TranslationKey;
  const display = key in en ? t(key) : label.startsWith("Unknown") ? label.replace("Unknown", t("common.unknown")) : label;
  return <span className={`status status--${tone(value, domain)}`}><span />{display}</span>;
}

export function Loading({ label }: { label?: string }) {
  const { t } = usePreferences();
  return <div className="state state--loading"><LoaderCircle className="spin" /><p>{label ?? t("common.loading")}</p></div>;
}

export function Empty({ title, detail, action }: { title: string; detail: string; action?: ReactNode }) {
  return <div className="state"><Inbox /><h3>{title}</h3><p>{detail}</p>{action}</div>;
}

export function ErrorState({ error, retry }: { error: unknown; retry?: () => void }) {
  const { t } = usePreferences();
  const message = error instanceof Error ? error.message : t("common.unexpectedError");
  return (
    <div className="state state--error" role="alert">
      <AlertTriangle /><h3>{t("common.loadErrorTitle")}</h3><p>{message}</p>
      {retry && <button className="button button--secondary" onClick={retry}><RefreshCw /> {t("common.tryAgain")}</button>}
    </div>
  );
}

export function Notice({ kind, children, onClose }: {
  kind: "success" | "danger"; children: ReactNode; onClose?: () => void;
}) {
  const { t } = usePreferences();
  const Icon = kind === "success" ? CheckCircle2 : XCircle;
  return <div className={`notice notice--${kind}`} role={kind === "danger" ? "alert" : "status"}><Icon />{children}{onClose && <button onClick={onClose} aria-label={t("common.dismiss")}>×</button>}</div>;
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
  const { t } = usePreferences();
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={event => event.target === event.currentTarget && onClose()}>
      <section className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title">
        <header><div><span className="eyebrow">{t("common.configuration")}</span><h2 id="modal-title">{title}</h2></div><button className="icon-button" onClick={onClose} aria-label={t("common.close")}>×</button></header>
        {children}
      </section>
    </div>
  );
}
