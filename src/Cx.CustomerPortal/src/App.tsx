import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, NavLink, Navigate, Route, Routes } from "react-router-dom";
import {
  request,
  submitVoice,
  type Catalog,
  type Kind,
  type Submission,
  type PortalUser,
  ApiError,
} from "./api";
import type { PreparedAudio } from "./audio";
import VoiceRecorder from "./VoiceRecorder";
import { Tracking, TrackingDetail, Surveys, PortalNotifications } from "./Operations";

const sections: {
  kind: Kind;
  path: string;
  label: string;
  icon: string;
  color: string;
}[] = [
  {
    kind: "complaint",
    path: "/complaints",
    label: "Complaints",
    icon: "!",
    color: "coral",
  },
  {
    kind: "feedback",
    path: "/feedbacks",
    label: "Feedback",
    icon: "↗",
    color: "gold",
  },
  {
    kind: "voc",
    path: "/voice-of-customer",
    label: "Voice of customer",
    icon: "≋",
    color: "green",
  },
];

export default function App() {
  const [user, setUser] = useState<PortalUser | null>(null);
  const [checking, setChecking] = useState(true);
  const [sessionError, setSessionError] = useState("");
  const [reload, setReload] = useState(0);
  const [signingOut, setSigningOut] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    setChecking(true);
    setSessionError("");
    request<PortalUser>("auth/me", undefined, controller.signal)
      .then(setUser)
      .catch((err) => {
        if (
          !controller.signal.aborted &&
          !(err instanceof ApiError && err.status === 401)
        )
          setSessionError(err.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setChecking(false);
      });
    return () => controller.abort();
  }, [reload]);
  useEffect(() => {
    const expired = () => setUser(null);
    window.addEventListener("portal-session-expired", expired);
    return () => window.removeEventListener("portal-session-expired", expired);
  }, []);
  async function logout() {
    setSigningOut(true);
    try {
      await request("auth/logout", {});
      setUser(null);
      setSessionError("");
    } catch (err) {
      setSessionError(err instanceof Error ? err.message : "Exit failed.");
    } finally {
      setSigningOut(false);
    }
  }
  return (
    <div className="portal">
      <a className="skip" href="#content">
        Skip to content
      </a>
      <main id="content">
        {checking ? (
          <div className="loading" role="status">
            Checking account…
          </div>
        ) : !user ? (
          sessionError ? (
            <div className="error" role="alert">
              {sessionError}
              <button onClick={() => setReload((v) => v + 1)}>
                Retry
              </button>
            </div>
          ) : (
            <CustomerLogin onLogin={setUser} />
          )
        ) : (
          <>
            <div className="account-bar">
              <span>{user.display_name}</span>
              <button type="button" onClick={logout} disabled={signingOut}>
                Sign out
              </button>
            </div>
            {sessionError && (
              <div className="error" role="alert">
                {sessionError}
              </div>
            )}
            <nav className="portal-operations-nav" aria-label="Customer service">
              <NavLink to="/feedbacks">Submit feedback</NavLink><NavLink to="/tracking">Pursuing requests</NavLink>
              <NavLink to="/surveys">Surveys</NavLink><NavLink to="/notifications">Notifications</NavLink>
            </nav>
            <Routes>
              <Route path="/" element={<Navigate to="/feedbacks" replace />} />
              <Route path="/tracking" element={<Tracking />} />
              <Route path="/tracking/:caseId" element={<TrackingDetail />} />
              <Route path="/surveys" element={<Surveys />} />
              <Route path="/surveys/:invitationId" element={<Surveys />} />
              <Route path="/notifications" element={<PortalNotifications />} />
              {sections.map((section) => (
                <Route
                  key={section.kind}
                  path={section.path}
                  element={
                    <CustomerForm key={section.kind} kind={section.kind} />
                  }
                />
              ))}
              <Route
                path="*"
                element={
                  <div className="not-found">
                    <h1>Page not found</h1>
                    <Link to="/">Returning home</Link>
                  </div>
                }
              />
            </Routes>
          </>
        )}
      </main>
      <footer>
        <span>Every experience is worth hearing.</span>
        <span>Complaints · Feedback · Voice of customer</span>
      </footer>
    </div>
  );
}

function CustomerLogin({ onLogin }: { onLogin: (user: PortalUser) => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function login(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      onLogin(await request<PortalUser>("auth/login", { username, password }));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login failed.");
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="form-card login-card">
      <h1>Entering the customer portal</h1>
      <form onSubmit={login}>
        <fieldset className="form-fields" disabled={busy}>
          <label className="field">
            <span>Username</span>
            <input
              required
              maxLength={128}
              autoComplete="username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoFocus
            />
          </label>
          <label className="field">
            <span>Password</span>
            <input
              required
              type="password"
              maxLength={256}
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </label>
          {error && (
            <div className="error" role="alert">
              {error}
            </div>
          )}
          <button className="submit" type="submit">
            {busy ? "Entering…" : "Sign in"}
          </button>
        </fieldset>
      </form>
    </section>
  );
}

function Scale({
  label,
  value,
  onChange,
  nps = false,
  ces = false,
}: {
  label: string;
  value: number | null;
  onChange: (v: number) => void;
  nps?: boolean;
  ces?: boolean;
}) {
  const values = Array.from({ length: nps ? 11 : ces ? 7 : 5 }, (_, i) =>
    nps ? i : i + 1,
  );
  return (
    <fieldset className="scale">
      <legend>
        {label} <span className="required">*</span>
      </legend>
      <div className={nps ? "scores nps" : ces ? "scores ces" : "scores"} dir="ltr">
        {values.map((n) => (
          <label key={n} className={value === n ? "chosen" : ""}>
            <input
              type="radio"
              name={label}
              value={n}
              checked={value === n}
              onChange={() => onChange(n)}
              required
            />
            <span>
              {nps || ces
                ? n
                : ["Very unhappy", "Dissatisfied", "Medium", "Satisfied", "Very satisfied"][
                    n - 1
                  ]}
            </span>
          </label>
        ))}
      </div>
      <div className="scale-captions">
        <span>{nps ? "I definitely recommend" : ces ? "7: Very easy" : "5: Very satisfied"}</span>
        <span>{nps ? "I do not recommend" : ces ? "1: Very difficult" : "1: Very unhappy"}</span>
      </div>
    </fieldset>
  );
}

function CustomerForm({ kind }: { kind: Kind }) {
  const section = sections.find((s) => s.kind === kind)!;
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [reload, setReload] = useState(0);
  const [category, setCategory] = useState("");
  const [subject, setSubject] = useState("");
  const [comment, setComment] = useState("");
  const [rating, setRating] = useState<number | null>(null);
  const [nps, setNps] = useState<number | null>(null);
  const [ces, setCes] = useState<number | null>(null);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  // Complaints and feedback can be told by voice instead of written; the questionnaire cannot.
  const [mode, setMode] = useState<"text" | "voice">("text");
  const [audio, setAudio] = useState<PreparedAudio | null>(null);
  const byVoice = kind !== "voc" && mode === "voice";
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [receipt, setReceipt] = useState("");
  const [copied, setCopied] = useState(false);
  // The same id and payload are retained after a network failure to make retries idempotent.
  const pending = useRef<Submission | null>(null);
  const doneRef = useRef<HTMLDivElement>(null);
  const selected = catalog?.categories.find((c) => c.id === category);

  useEffect(() => {
    document.title = `${section.label} | Your voice`;
    const controller = new AbortController();
    setLoading(true);
    setLoadError("");
    request<Catalog>("categories", undefined, controller.signal)
      .then(setCatalog)
      .catch((err) => {
        if (!controller.signal.aborted) setLoadError(err.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [reload, section.label]);
  useEffect(() => {
    if (receipt) doneRef.current?.focus();
  }, [receipt]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy || !selected || !catalog) return;
    const voice = byVoice && audio;
    if (byVoice && !audio) {
      setError("First, record your voice message.");
      return;
    }
    setError("");
    setBusy(true);
    const payload: Submission = {
      submission_id: pending.current?.submission_id || crypto.randomUUID(),
      kind,
      category,
      revision: catalog.revision,
      subject,
      comment: byVoice ? "" : comment,
      rating,
      nps_score: nps,
      ces_score: kind === "voc" ? (answers.effort ? Number(answers.effort) : null) : ces,
      answers,
    };
    pending.current ??= payload;
    try {
      const result = voice
        ? await submitVoice<{ receipt: string }>(pending.current, voice)
        : await request<{ receipt: string }>("submissions", pending.current);
      setReceipt(result.receipt);
    } catch (err) {
      setError(
        err instanceof Error
          ? err.message
          : "Sending was not done; Try again.",
      );
    } finally {
      setBusy(false);
    }
  }
  function reset() {
    pending.current = null;
    setReceipt("");
    setCategory("");
    setSubject("");
    setComment("");
    setAnswers({});
    setRating(null);
    setNps(null);
    setCes(null);
    setMode("text");
    setAudio(null);
    setError("");
    setCopied(false);
    window.scrollTo({ top: 0, behavior: "smooth" });
  }
  const locked = busy || pending.current !== null;

  return (
    <>
      <nav className="section-nav" aria-label="Departments of communication with the customer">
        {sections.map((s) => (
          <NavLink
            key={s.kind}
            to={s.path}
            className={({ isActive }) =>
              `section-link ${s.color} ${isActive ? "active" : ""}`
            }
          >
            <span className="section-icon" aria-hidden="true">
              {s.icon}
            </span>
            <span>
              {s.label}
              <small>
                {s.kind === "complaint"
                  ? "Registration and follow-up of the problem"
                  : s.kind === "feedback"
                    ? "Your comments and suggestions"
                    : "Customer experience questionnaire"}
              </small>
            </span>
            <span className="nav-arrow" aria-hidden="true">
              ↗
            </span>
          </NavLink>
        ))}
      </nav>
      <div className="content-grid">
        <section className="form-card">
          {receipt ? (
            <div className="success" role="status" ref={doneRef} tabIndex={-1}>
              <span className="success-check">✓</span>
              <p className="eyebrow">Thank you for sharing with us</p>
              <h2>
                {kind === "complaint"
                  ? "Your complaint has been received."
                  : kind === "voc"
                    ? "Your responses have been received."
                    : "Your feedback has been received."}
              </h2>
              <p>
                {byVoice
                  ? "Your voice message has been received; Converted to text and sent for review."
                  : "Your request has been received and sent for storage and review."}
              </p>
              <div className="receipt">
                <span>Request registration code</span>
                <strong dir="ltr">{receipt}</strong>
                <button
                  type="button"
                  onClick={async () => {
                    try {
                      await navigator.clipboard.writeText(receipt);
                      setCopied(true);
                    } catch {
                      setError("Copy the code manually.");
                    }
                  }}
                >
                  {copied ? "Copied" : "Copy the code"}
                </button>
              </div>
              <p className="subtle">
                Keep this code with you for future reference.
              </p>
              {error && <p role="alert">{error}</p>}
              <button className="submit" onClick={reset}>
                Record another experience <span>→</span>
              </button>
            </div>
          ) : (
            <>
              <div className="form-heading">
                <span className={`section-icon ${section.color}`}>
                  {section.icon}
                </span>
                <div>
                  <h2>
                    {kind === "complaint"
                      ? "Submit complaint"
                      : kind === "voc"
                        ? "Customer survey"
                        : "Submit feedback"}
                  </h2>
                  <p>
                    {kind === "voc"
                      ? "Select the topic; Questions are displayed based on the same topic."
                      : "A few minutes of your time, a step for our betterment."}
                  </p>
                </div>
                <span className="time">
                  ◷ {kind === "voc" ? "3" : "2"} minutes
                </span>
              </div>
              {loading ? (
                <div className="loading" role="status">
                  Receiving topics…
                </div>
              ) : loadError ? (
                <div className="error" role="alert">
                  <p>{loadError}</p>
                  <button onClick={() => setReload((v) => v + 1)}>
                    Retry
                  </button>
                </div>
              ) : !catalog?.categories.length ? (
                <div className="empty">
                  <h3>No subject has yet been defined to record the experience.</h3>
                  <p>
                    Please again after the categories are defined by the support team
                    See.
                  </p>
                  <button onClick={() => setReload((v) => v + 1)}>
                    Review again
                  </button>
                </div>
              ) : (
                <form onSubmit={submit}>
                  <fieldset disabled={locked} className="form-fields">
                    <label className="field">
                      <span>
                        What is your experience related to?{" "}
                        <b className="required">*</b>
                      </span>
                      <select
                        required
                        value={category}
                        onChange={(e) => {
                          setCategory(e.target.value);
                          setAnswers({});
                        }}
                      >
                        <option value="">Subject selection</option>
                        {catalog.categories.map((c) => (
                          <option key={c.id} value={c.id}>
                            {c.path}
                          </option>
                        ))}
                      </select>
                      {selected?.description && (
                        <small>{selected.description}</small>
                      )}
                    </label>
                    {kind === "complaint" && (
                      <label className="field">
                        <span>
                          Title of complaint <b className="required">*</b>
                        </span>
                        <input
                          required
                          maxLength={160}
                          value={subject}
                          onChange={(e) => setSubject(e.target.value)}
                          placeholder="For example: Difficulty in receiving services"
                        />
                      </label>
                    )}
                    {kind === "voc" ? (
                      selected ? (
                        <div className="questionnaire">
                          <p className="questionnaire-title">
                            <span>✦</span> About «{selected.title}» More
                            Say
                          </p>
                          {selected.questions.map((q, index) =>
                            q.type === "rating" || q.type === "effort" ? (
                              <Scale
                                key={q.id}
                                label={q.label}
                                ces={q.type === "effort"}
                                value={
                                  answers[q.id] ? Number(answers[q.id]) : null
                                }
                                onChange={(v) =>
                                  setAnswers((a) => ({
                                    ...a,
                                    [q.id]: String(v),
                                  }))
                                }
                              />
                            ) : (
                              <label className="field" key={q.id}>
                                <span>
                                  <b className="question-number">{index + 1}</b>{" "}
                                  {q.label} <b className="required">*</b>
                                </span>
                                <textarea
                                  required={q.required}
                                  maxLength={2000}
                                  rows={3}
                                  value={answers[q.id] || ""}
                                  onChange={(e) =>
                                    setAnswers((a) => ({
                                      ...a,
                                      [q.id]: e.target.value,
                                    }))
                                  }
                                  placeholder="Write in your own language…"
                                />
                              </label>
                            ),
                          )}
                          <Scale
                            nps
                            label="How likely are you to recommend our services to others?"
                            value={nps}
                            onChange={setNps}
                          />
                        </div>
                      ) : (
                        <div className="question-placeholder">
                          Your questions will be displayed after selecting the topic.
                        </div>
                      )
                    ) : (
                      <>
                        <div
                          className="mode-switch"
                          role="radiogroup"
                          aria-label="The method of expressing experience"
                        >
                          {(["text", "voice"] as const).map((m) => (
                            <button
                              key={m}
                              type="button"
                              role="radio"
                              aria-checked={mode === m}
                              className={mode === m ? "active" : ""}
                              onClick={() => {
                                setMode(m);
                                setError("");
                              }}
                            >
                              <span aria-hidden="true">
                                {m === "text" ? "✎" : "●"}
                              </span>
                              {m === "text" ? "Writing" : "Audio recording"}
                            </button>
                          ))}
                        </div>
                        {byVoice ? (
                          <div className="field">
                            <span>
                              {kind === "complaint"
                                ? "Tell me what happened and what you expect."
                                : "Say whatever you want to share with us."}{" "}
                              <b className="required">*</b>
                            </span>
                            <VoiceRecorder
                              value={audio}
                              onChange={(recorded) => {
                                setAudio(recorded);
                                if (recorded) setError("");
                              }}
                              disabled={locked}
                            />
                            <small>
                              Your voice will be converted to text along with the audio file
                              It is registered for review.
                            </small>
                          </div>
                        ) : (
                          <label className="field">
                            <span>
                              {kind === "complaint"
                                ? "What happened and what do you expect?"
                                : "What do you want to share with us?"}{" "}
                              <b className="required">*</b>
                            </span>
                            <textarea
                              required
                              rows={5}
                              maxLength={4000}
                              value={comment}
                              onChange={(e) => setComment(e.target.value)}
                              placeholder={
                                kind === "complaint"
                                  ? "Explain the problem, when it occurred and the result you expect…"
                                  : "Write your experience, suggestion or comment here…"
                              }
                            />
                            <small className="counter">
                              {comment.length.toLocaleString("en-US")} / 4000
                            </small>
                          </label>
                        )}
                        {kind === "feedback" && (
                          <>
                          <Scale
                            label="Overall, how satisfied are you with your experience?"
                            value={rating}
                            onChange={setRating}
                          />
                          <Scale
                            ces
                            label="How easy was it to do the job or get the service?"
                            value={ces}
                            onChange={setCes}
                          />
                          </>
                        )}
                      </>
                    )}
                  </fieldset>
                  {error && (
                    <div className="error" role="alert">
                      {error}
                    </div>
                  )}
                  <div className="form-bottom">
                    <button
                      type="submit"
                      className="submit"
                      disabled={busy || !selected}
                    >
                      {busy
                        ? "Sending…"
                        : pending.current
                          ? "Retrying to send"
                          : byVoice
                            ? "Sending a voice message"
                            : kind === "complaint"
                              ? "Send a complaint"
                              : kind === "voc"
                                ? "Sending the questionnaire for analysis"
                                : "Send feedback"}
                      <span>→</span>
                    </button>
                    <small>Fields with an asterisk are required.</small>
                  </div>
                  {pending.current && !busy && (
                    <button
                      className="start-over"
                      type="button"
                      onClick={() => {
                        pending.current = null;
                        setError("");
                        setReload((v) => v + 1);
                      }}
                    >
                      Editing answers and starting a new post
                    </button>
                  )}
                </form>
              )}
            </>
          )}
        </section>
      </div>
    </>
  );
}
