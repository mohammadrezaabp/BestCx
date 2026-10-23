import { useState } from "react";
import { ApiError, api, type SessionUser } from "../api";
import { ErrorBox } from "../components/Common";
import Icon from "../components/Icon";

export default function Login({ onSignedIn, error: initialError }: {
  onSignedIn: (user: SessionUser) => void;
  error?: unknown;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(initialError ?? null);
  const [reveal, setReveal] = useState(false);

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const username = String(form.get("username") ?? "").trim();
    const password = String(form.get("password") ?? "");
    if (!username || !password) {
      setError(new Error("Enter username and password."));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.login(username, password));
    } catch (err) {
      // A 401 here means wrong credentials, not an expired session: show the server's reason.
      setError(err instanceof ApiError && err.status !== 403 ? new Error(err.message) : err);
      setBusy(false);
    }
  }

  return (
    <div className="login">
      <aside className="login-brand" aria-hidden="true">
        <span className="brand-mark large">
          <i />
          <i />
          <i />
        </span>
        <strong>BestCx</strong>
      </aside>
      <main className="login-panel">
        <form className="login-form" onSubmit={submit} noValidate>
          <h1>Sign in</h1>
          <ErrorBox error={error} />
          <fieldset disabled={busy} className="plain-fieldset">
            <label className="field">
              <span>Username</span>
              <input name="username" autoComplete="username" dir="ltr" autoFocus required />
            </label>
            <label className="field">
              <span>Password</span>
              <span className="password-input">
                <input
                  name="password"
                  type={reveal ? "text" : "password"}
                  autoComplete="current-password"
                  dir="ltr"
                  required
                />
                <button
                  type="button"
                  className="icon-button"
                  aria-label={reveal ? "Hiding the password" : "Display password"}
                  aria-pressed={reveal}
                  onClick={() => setReveal((r) => !r)}
                >
                  <Icon name={reveal ? "eye-off" : "eye"} size={16} />
                </button>
              </span>
            </label>
            <button className="primary login-submit" type="submit">
              {busy ? <span className="spinner small" /> : null}
              {busy ? "Entering…" : "Sign in"}
            </button>
          </fieldset>
        </form>
      </main>
    </div>
  );
}
