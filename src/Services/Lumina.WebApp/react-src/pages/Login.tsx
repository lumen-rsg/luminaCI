import { ArrowRight, KeyRound, LockKeyhole, ShieldCheck, UserRound } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useAuth } from "../auth/AuthContext";
import { Brand } from "../components/Brand";
import { usePreferences } from "../i18n/PreferencesContext";
import { Navigate, useLocation } from "../lib/router";

export function Login() {
  const { user, login } = useAuth();
  const { t, locale, setLocale } = usePreferences();
  const location = useLocation();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  if (user) return <Navigate to={(location.state as { from?: string } | null)?.from || "/"} replace />;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      await login(username.trim(), password);
    } catch (reason) {
      setError(reason instanceof Error && reason.message ? reason.message : t("login.error"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="login-page">
      <section className="login-story" aria-label={t("login.about")}>
        <Brand />
        <div className="login-story__content">
          <span className="eyebrow">{t("login.eyebrow")}</span>
          <h1>{t("login.headline1")}<br /><em>{t("login.headline2")}</em></h1>
          <p>{t("login.story")}</p>
          <div className="login-proof">
            <span><ShieldCheck /> {t("login.policy")}</span>
            <span><KeyRound /> {t("login.signed")}</span>
            <span><LockKeyhole /> {t("login.private")}</span>
          </div>
        </div>
        <small>console.lumina.1t.ru · {t("login.workspace")}</small>
      </section>
      <section className="login-panel">
        <div className="login-locale" aria-label={t("settings.language")}>
          <button className={locale === "en" ? "active" : ""} onClick={() => setLocale("en")}>EN</button>
          <button className={locale === "ru" ? "active" : ""} onClick={() => setLocale("ru")}>RU</button>
        </div>
        <div className="login-card">
          <span className="eyebrow">{t("login.secureConsole")}</span>
          <h2>{t("login.welcome")}</h2>
          <p>{t("login.continue")}</p>
          <form onSubmit={submit}>
            <label htmlFor="username">{t("login.username")}</label>
            <div className="input-wrap"><UserRound /><input id="username" autoComplete="username" required autoFocus value={username} onChange={event => setUsername(event.target.value)} placeholder={t("login.usernamePlaceholder")} /></div>
            <label htmlFor="password">{t("login.password")}</label>
            <div className="input-wrap"><LockKeyhole /><input id="password" type="password" autoComplete="current-password" required value={password} onChange={event => setPassword(event.target.value)} placeholder={t("login.passwordPlaceholder")} /></div>
            {error && <div className="form-error" role="alert">{error}</div>}
            <button className="button button--primary button--wide" disabled={busy}>{busy ? t("login.authenticating") : <>{t("login.signIn")} <ArrowRight /></>}</button>
          </form>
          <div className="login-card__footer"><ShieldCheck /> {t("login.security")}</div>
        </div>
      </section>
    </main>
  );
}
