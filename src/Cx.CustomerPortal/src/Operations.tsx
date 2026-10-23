import { useEffect, useState, type ReactNode } from "react";
import { Link, useParams } from "react-router-dom";
import { request, uploadCaseFile } from "./api";

interface PortalCase { id: string; reference: string; title: string; priority: string; status: string; version: number; created_at: string; updated_at: string; resolution_text: string; reopen_count: number; }
interface PublicActivity { id: string; kind: string; actor: string; text: string; created_at: string; }
interface PublicAttachment { id: string; filename: string; size_bytes: number; created_at: string; }
interface PublicDetail { case: PortalCase; activities: PublicActivity[]; attachments: PublicAttachment[]; }
interface HistoryItem { id: string; comment: string; feedback_type: string; submitted_at: string; status: string; case_id: string | null; }
interface Invitation { id: string; campaign_name: string; question: string; metric: string; expires_at: string; answered_at: string | null; declined_at: string | null; score: number | null; }
interface Notice { id: string; title: string; body: string; link: string; created_at: string; read_at: string | null; }
interface Paged<T> { items: T[]; total: number; page: number; page_size: number; }
const statuses: Record<string, string> = { new: "Received", in_progress: "In progress", waiting_customer: "Waiting for your reply", resolved: "Resolved; Waiting for your confirmation", closed: "Closed" };
const dates = new Intl.DateTimeFormat("en-US", { timeZone: "UTC", dateStyle: "medium", timeStyle: "short" });
const numbers = new Intl.NumberFormat("en-US");
function date(value: string) { return dates.format(new Date(value)); }
function useData<T>(path: string) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError("");
    request<T>(path, undefined, controller.signal).then(value => { if (!controller.signal.aborted) setData(value); }).catch(err => { if (!controller.signal.aborted) setError(err.message); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [path, nonce]);
  return { data, error, loading, reload: () => setNonce(value => value + 1) };
}
function AsyncView({ loading, error, children }: { loading: boolean; error: string; children: ReactNode }) {
  return error ? <div className="error" role="alert">{error}</div> : loading ? <div className="loading" role="status">Receiving…</div> : <>{children}</>;
}

export function Tracking() {
  const [casePage, setCasePage] = useState(1);
  const [historyPage, setHistoryPage] = useState(1);
  const cases = useData<Paged<PortalCase>>(`cases?page=${casePage}`);
  const history = useData<Paged<HistoryItem>>(`history?page=${historyPage}`);
  return <section className="form-card portal-operations"><div className="portal-section-head"><h1>Pursuing requests</h1><button onClick={() => { cases.reload(); history.reload(); }}>Update</button></div>
    <AsyncView loading={cases.loading} error={cases.error}>
      {!cases.data?.items.length ? <p className="portal-empty">You do not have a follow-up file yet.</p> : <div className="portal-case-list">{cases.data.items.map(item => <Link key={item.id} to={`/tracking/${item.id}`} className="portal-case-card">
        <div><strong>{item.title}</strong><span className={`portal-status ${item.status}`}>{statuses[item.status]}</span></div><small>{item.reference} · {date(item.created_at)}</small>
        {item.status === "resolved" && <span className="portal-followup-hint">Check the result and confirm that the problem is solved.</span>}
      </Link>)}</div>}
      {cases.data && <Pagination page={casePage} total={cases.data.total} onPage={setCasePage} />}
    </AsyncView>
    <h2>Applications filed</h2><AsyncView loading={history.loading} error={history.error}>
      {!history.data?.items.length ? <p className="portal-empty">You have not registered a feedback yet.</p> : <div className="portal-history">{history.data.items.map(item => <article key={item.id}><time>{date(item.submitted_at)}</time><p>{item.comment}</p>{item.case_id ? <Link to={`/tracking/${item.case_id}`}>Observation of proceedings</Link> : <span className="portal-status">{item.status === "closed" ? "Closed" : "Received"}</span>}</article>)}</div>}
      {history.data && <Pagination page={historyPage} total={history.data.total} onPage={setHistoryPage} />}
    </AsyncView>
  </section>;
}

export function TrackingDetail() {
  const { caseId = "" } = useParams();
  const detail = useData<PublicDetail>(`cases/${caseId}`);
  const [text, setText] = useState("");
  const [reason, setReason] = useState("");
  const [reopening, setReopening] = useState(false);
  const [messageId, setMessageId] = useState(() => crypto.randomUUID());
  const [fileId, setFileId] = useState(() => crypto.randomUUID());
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function run(action: () => Promise<unknown>) {
    if (busy) return; setBusy(true); setError("");
    try { await action(); detail.reload(); } catch (err) { setError(err instanceof Error ? err.message : "Registration failed."); } finally { setBusy(false); }
  }
  const item = detail.data?.case;
  return <section className="form-card portal-operations"><div className="portal-section-head"><h1>{item?.title ?? "Pursuing the request"}</h1><Link to="/tracking">My requests</Link></div>
    <AsyncView loading={detail.loading} error={detail.error}>{item && <>
      <div className="portal-case-meta"><span>{item.reference}</span><span className={`portal-status ${item.status}`}>{statuses[item.status]}</span><time>{date(item.created_at)}</time></div>
      {item.resolution_text && <section className="portal-resolution"><h2>{item.status === "resolved" || item.status === "closed" ? "Service outcomes" : "The result of the previous proceedings"}</h2><p>{item.resolution_text}</p></section>}
      <ol className="portal-case-timeline">{detail.data?.activities.map(activity => <li key={activity.id}><div><strong>{activity.actor}</strong><time>{date(activity.created_at)}</time></div><p>{activity.text}</p></li>)}</ol>
      {!!detail.data?.attachments.length && <section><h2>Attachments</h2><ul className="portal-files">{detail.data.attachments.map(attachment => <li key={attachment.id}><a href={`/v1/portal/cases/${caseId}/attachments/${attachment.id}`}>{attachment.filename}</a><small>{numbers.format(Math.ceil(attachment.size_bytes / 1024))} KB</small></li>)}</ul></section>}
      {item.status === "resolved" && <div className="portal-case-actions"><button className="submit" disabled={busy} onClick={() => void run(async () => request(`cases/${caseId}/action`, { version: item.version, action: "confirm" }))}>The problem is solved</button><button disabled={busy} onClick={() => setReopening(true)}>The problem still remains</button></div>}
      {item.status === "closed" && <div className="portal-case-actions"><button disabled={busy} onClick={() => setReopening(true)}>Reopening request</button></div>}
      {reopening && <form onSubmit={event => { event.preventDefault(); void run(async () => { await request(`cases/${caseId}/action`, { version: item.version, action: "reopen", comment: reason }); setReopening(false); setReason(""); }); }}>
        <label className="field"><span>Reason for reopening</span><textarea required maxLength={12000} rows={4} value={reason} disabled={busy} onChange={event => setReason(event.target.value)} /></label><div className="portal-case-actions"><button className="submit" disabled={busy}>Open registration</button><button type="button" disabled={busy} onClick={() => setReopening(false)}>Cancel</button></div>
      </form>}
      {item.status !== "resolved" && item.status !== "closed" && <form className="portal-reply" onSubmit={event => { event.preventDefault(); void run(async () => {
        if (text.trim()) await request(`cases/${caseId}/messages`, { id: messageId, text: text.trim() });
        if (file) await uploadCaseFile(caseId, file, fileId);
        setText(""); setFile(null); setMessageId(crypto.randomUUID()); setFileId(crypto.randomUUID());
      }); }}><fieldset disabled={busy}>
        <label className="field"><span>Your response or additional information</span><textarea maxLength={12000} rows={4} value={text} onChange={event => setText(event.target.value)} /></label>
        <label className="field"><span>Attached file (Maximum 10 MB)</span><input type="file" onChange={event => { setFile(event.target.files?.[0] ?? null); setFileId(crypto.randomUUID()); }} /></label>
      </fieldset><button className="submit" disabled={busy || (!text.trim() && !file)}>Send response</button></form>}
    </>}</AsyncView>{error && <div className="error" role="alert">{error}<button type="button" onClick={detail.reload}>Update information</button></div>}
  </section>;
}

export function Surveys() {
  const { invitationId } = useParams();
  const data = useData<Invitation[]>("surveys");
  const [selected, setSelected] = useState<Invitation | null>(null);
  const [score, setScore] = useState<number | null>(null);
  const [comment, setComment] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => {
    if (!invitationId) return;
    const controller = new AbortController();
    request<Invitation>(`surveys/${invitationId}`, undefined, controller.signal).then(setSelected).catch(err => { if (!controller.signal.aborted) setError(err.message); });
    return () => controller.abort();
  }, [invitationId, data.data]);
  async function act(action: () => Promise<unknown>) {
    if (busy) return; setBusy(true); setError("");
    try { await action(); setSelected(null); setScore(null); setComment(""); data.reload(); } catch (err) { setError(err instanceof Error ? err.message : "The answer was not recorded."); } finally { setBusy(false); }
  }
  const pending = (item: Invitation) => !item.answered_at && !item.declined_at && new Date(item.expires_at).getTime() > Date.now();
  return <section className="form-card portal-operations"><div className="portal-section-head"><h1>Your polls</h1><button onClick={data.reload}>Update</button></div>
    {notice && <div className="success-message" role="status">{notice}</div>}{error && <div className="error" role="alert">{error}</div>}
    <AsyncView loading={data.loading} error={data.error}>
      {!data.data?.length ? <p className="portal-empty">You do not have a survey invitation.</p> : <div className="portal-survey-list">{data.data.map(item => <article className="portal-case-card" key={item.id}><h2>{item.campaign_name}</h2><p>{item.question}</p>
        <small>Response deadline: {date(item.expires_at)}</small>{pending(item) ? <div className="portal-case-actions"><button className="submit" onClick={() => { setSelected(item); setScore(null); setComment(""); }}>Answer to the survey</button><button disabled={busy} onClick={() => void act(async () => { await request(`surveys/${item.id}/decline`, {}); setNotice("The cancellation of this invitation was recorded."); })}>Cancellation of this invitation</button></div>
          : <span className="portal-status">{item.answered_at ? `Your answer: ${numbers.format(item.score ?? 0)}` : item.declined_at ? "You have opted out" : "The deadline has expired"}</span>}
      </article>)}</div>}
      {selected && pending(selected) && <form className="portal-survey-answer" onSubmit={event => { event.preventDefault(); if (score === null) return; void act(async () => { await request(`surveys/${selected.id}/answer`, { score, comment }); setNotice("Your answer has been recorded. Thank you for your cooperation."); }); }}>
        <h2>{selected.question}</h2><fieldset disabled={busy}><legend>Your score</legend><div className="portal-score-options">
          {Array.from({ length: selected.metric === "nps" ? 11 : selected.metric === "ces" ? 7 : 5 }, (_, index) => selected.metric === "nps" ? index : index + 1).map(value => <label key={value} className={score === value ? "selected" : ""}><input type="radio" name="survey_score" required checked={score === value} onChange={() => setScore(value)} />{numbers.format(value)}</label>)}
        </div><p className="portal-score-scale">{selected.metric === "nps" ? "0: I do not recommend at all · 10: I definitely recommend" : selected.metric === "ces" ? "1: Very difficult · 7: Very easy" : "1: Very unhappy · 5: Very satisfied"}</p>
        <label className="field"><span>Your explanation (Optional)</span><textarea maxLength={12000} rows={3} value={comment} onChange={event => setComment(event.target.value)} /></label></fieldset>
        <div className="portal-case-actions"><button className="submit" disabled={busy || score === null}>Answer registration</button><button type="button" disabled={busy} onClick={() => setSelected(null)}>Cancel</button></div>
      </form>}
    </AsyncView>
  </section>;
}

export function PortalNotifications() {
  const data = useData<Notice[]>("notifications");
  const [error, setError] = useState("");
  return <section className="form-card portal-operations"><div className="portal-section-head"><h1>Your notifications</h1><button onClick={data.reload}>Update</button></div>
    {error && <div className="error" role="alert">{error}</div>}<AsyncView loading={data.loading} error={data.error}>
      {!data.data?.length ? <p className="portal-empty">You have no notification.</p> : <div className="portal-survey-list">{data.data.map(item => <article className={`portal-case-card${item.read_at ? "" : " unread"}`} key={item.id}><h2>{item.title}</h2><p>{item.body}</p><small>{date(item.created_at)}</small><div className="portal-case-actions"><Link to={item.link}>Observation</Link>{!item.read_at && <button onClick={async () => { try { await request(`notifications/${item.id}/read`, {}); data.reload(); } catch (err) { setError(err instanceof Error ? err.message : "Not registered."); } }}>It was read</button>}</div></article>)}</div>}
    </AsyncView>
  </section>;
}
function Pagination({ page, total, onPage }: { page: number; total: number; onPage: (page: number) => void }) {
  if (total <= 20) return null;
  return <div className="portal-case-actions"><button disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</button><span>Page {numbers.format(page)} of {numbers.format(Math.ceil(total / 20))}</span><button disabled={page * 20 >= total} onClick={() => onPage(page + 1)}>Next</button></div>;
}
