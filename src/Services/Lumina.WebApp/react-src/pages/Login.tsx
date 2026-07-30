import { ArrowRight, KeyRound, LockKeyhole, ShieldCheck, UserRound } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useAuth } from "../auth/AuthContext";
import { Brand } from "../components/Brand";
import { Navigate, useLocation } from "../lib/router";

export function Login() {
  const { user, login } = useAuth();
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
      setError(reason instanceof Error && reason.message ? reason.message : "Unable to sign in. Check your credentials.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="login-page">
      <section className="login-story" aria-label="About Lumina CI">
        <Brand />
        <div className="login-story__content">
          <span className="eyebrow">Trusted delivery infrastructure</span>
          <h1>Build software<br />with <em>clarity.</em></h1>
          <p>One secure control plane for source provenance, reproducible builds, signing, scanning, and package delivery.</p>
          <div className="login-proof">
            <span><ShieldCheck /> Policy enforced</span>
            <span><KeyRound /> Signed artifacts</span>
            <span><LockKeyhole /> Private by default</span>
          </div>
        </div>
        <small>console.lumina.1t.ru · operational workspace</small>
      </section>
      <section className="login-panel">
        <div className="login-card">
          <span className="eyebrow">Secure console</span>
          <h2>Welcome back</h2>
          <p>Use your Lumina operator account to continue.</p>
          <form onSubmit={submit}>
            <label htmlFor="username">Username</label>
            <div className="input-wrap"><UserRound /><input id="username" autoComplete="username" required autoFocus value={username} onChange={event => setUsername(event.target.value)} placeholder="operator" /></div>
            <label htmlFor="password">Password</label>
            <div className="input-wrap"><LockKeyhole /><input id="password" type="password" autoComplete="current-password" required value={password} onChange={event => setPassword(event.target.value)} placeholder="Enter your password" /></div>
            {error && <div className="form-error" role="alert">{error}</div>}
            <button className="button button--primary button--wide" disabled={busy}>{busy ? "Authenticating…" : <>Sign in <ArrowRight /></>}</button>
          </form>
          <div className="login-card__footer"><ShieldCheck /> Credentials are sent over the secure same-origin session and never stored in the browser.</div>
        </div>
      </section>
    </main>
  );
}
