import { useEffect, useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api, parseResult } from "../api";
import { fmtDateTime, fmtNumber, fmtPercent, label, resolutionLabels, sentimentLabels, severityLabels, statusLabels } from "../format";
import { Badge, ErrorBox, Loading, useAsync } from "./Common";
import FollowUpTask from "./FollowUpTask";
import Icon from "./Icon";
import { NpsBadge } from "./Nps";
import { useTopics } from "../hooks/useTopics";
import { operations } from "../operationsApi";
import { feedbackComment, inboxFor, inboxPath, type InboxKind } from "../feedback";
import { voicePath } from "../workspace";

function VoicePlayer({ callId }: { callId: string }) {
  const audio = useAsync(() => api.audioUrl(callId), [callId]);
  if (audio.loading) return <div className="small muted">Preparing the sound…</div>;
  if (audio.error || !audio.data) return <ErrorBox error={audio.error} />;
  return <audio className="detail-audio" controls preload="metadata" src={audio.data.url} />;
}

/** One feedback in full: what the customer said, what the AI concluded, and what to do next. */
export default function FeedbackDetail({
  id,
  inbox,
  analysisError,
  onChanged,
}: {
  id: string;
  inbox: InboxKind;
  /** Set by the grid when the latest analysis job failed. */
  analysisError?: string | null;
  onChanged: (notice: string) => void;
}) {
  const navigate = useNavigate();
  const detail = useAsync(() => api.feedbackDetail(id), [id]);
  const workCase = useAsync(() => operations.forFeedback(id), [id]);
  const topics = useTopics();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [requeued, setRequeued] = useState(false);
  const [taskOpen, setTaskOpen] = useState(false);

  const item = detail.data?.feedback;
  const isComplaint = item?.feedback_type === "complaint";
  const noun = isComplaint ? "Complaint" : "Feedback";
  const wrongInbox = Boolean(item && inboxFor(item.feedback_type) !== inbox);
  const analysis = parseResult(detail.data?.analysis);
  const isVoice = Boolean(item?.call_id);
  const failed = Boolean(analysisError) && !requeued;
  const waiting = Boolean(item) && !failed && (!analysis || requeued);

  // Preserve old and externally shared links while opening the correct dedicated inbox.
  useEffect(() => {
    if (item && wrongInbox) {
      navigate(`${inboxPath(inboxFor(item.feedback_type))}?id=${encodeURIComponent(id)}`, { replace: true });
    }
  }, [item?.feedback_type, wrongInbox, id, navigate]);

  useEffect(() => {
    if (item?.feedback_type !== "complaint" || !item.customer_id || item.status === "closed" || workCase.data?.id) return;
    let polls = 0;
    const timer = window.setInterval(() => {
      if (++polls > 40) window.clearInterval(timer);
      else workCase.reload();
    }, 3000);
    return () => window.clearInterval(timer);
  }, [item?.feedback_type, item?.customer_id, item?.status, Boolean(workCase.data?.id), workCase.reload]);

  // Analysis runs in the background; check back until it lands instead of making the
  // operator refresh. Bounded so an abandoned drawer does not poll forever.
  useEffect(() => {
    if (!waiting) return;
    let polls = 0;
    const timer = window.setInterval(async () => {
      if (++polls > 40) return window.clearInterval(timer);
      try {
        const next = await api.feedbackDetail(id);
        const nextId = next.analysis?.id;
        if (nextId && nextId !== detail.data?.analysis?.id) {
          setRequeued(false);
          detail.reload();
          onChanged("Intelligent analysis was prepared.");
        } else if (next.feedback.comment !== item?.comment) {
          detail.reload();
        }
      } catch {
        /* the next tick retries */
      }
    }, 4000);
    return () => window.clearInterval(timer);
  }, [waiting, id, detail.data?.analysis?.id, item?.comment]);

  async function reanalyze() {
    setBusy(true);
    setError(null);
    try {
      const result = await api.reanalyzeFeedback(id);
      setRequeued(true);
      onChanged(
        result.scope === "transcript"
          ? "Transcription and analysis were again queued."
          : "The analysis was queued again.",
      );
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  // Rendered outside the loading branches: a status change reloads the detail, and the
  // dialog must not remount (and ask for a second draft) while it does.
  const taskDialog = taskOpen ? <FollowUpTask feedbackId={id} onClose={() => setTaskOpen(false)} /> : null;
  const frame = (body: ReactNode) => (
    <>
      {body}
      {taskDialog}
    </>
  );

  if (detail.loading && !detail.data) return frame(<Loading />);
  if (!item) return frame(<ErrorBox error={detail.error} />);
  if (wrongInbox) return <Loading />;

  const comment = feedbackComment(item.comment);
  const model = detail.data?.analysis;

  return frame(
    <div className="feedback-detail-body">
      <div className="detail-meta">
        {item.customer_id ? (
          <Link to={`/customers/${encodeURIComponent(item.customer_id)}`}>
            <Icon name="users" size={15} /> {detail.data?.customer_name || item.customer_id}
          </Link>
        ) : (
          <span className="muted">Unspecified customer</span>
        )}
        <time>{fmtDateTime(item.submitted_at)}</time>
        {isComplaint && <Badge value={item.status} />}
      </div>

      <dl className="detail-facts">
        <div>
          <dt>Type</dt>
          <dd>{isComplaint ? (isVoice ? "Voice complaints" : "Text complaint") : item.feedback_type === "voc" ? "Customer survey" : isVoice ? "Audio feedback" : "Textual feedback"}</dd>
        </div>
        <div>
          <dt>Channel</dt>
          <dd>{label(statusLabels, item.channel)}</dd>
        </div>
        {item.product_id && (
          <div>
            <dt>Product</dt>
            <dd>{item.product_id}</dd>
          </div>
        )}
      </dl>
      {!isComplaint && [item.rating, item.nps_score, item.csat_score, item.ces_score].some(score => score != null) && (
        <section aria-label="Feedback points">
          <h3>Customer scores</h3>
          <dl className="detail-facts">
            {item.rating != null && item.rating !== item.csat_score && <div><dt>Rating</dt><dd>{fmtNumber(item.rating)} From 5</dd></div>}
            {item.nps_score != null && <div><dt>Advice to others (NPS)</dt><dd><NpsBadge score={item.nps_score} /></dd></div>}
            {item.csat_score != null && <div><dt>Satisfaction (CSAT)</dt><dd>{fmtNumber(item.csat_score)} From 5</dd></div>}
            {item.ces_score != null && <div><dt>Effort (CES)</dt><dd>{fmtNumber(item.ces_score)} From 7</dd></div>}
          </dl>
        </section>
      )}

      {detail.data?.submission && (
        <dl className="detail-facts">
          {detail.data.submission.receipt && <div><dt>Registration code {noun}</dt><dd dir="ltr">{detail.data.submission.receipt}</dd></div>}
          {detail.data.submission.category_title && <div><dt>Topic {noun}</dt><dd>{detail.data.submission.category_title}</dd></div>}
          {detail.data.submission.name && <div><dt>Registrant’s name</dt><dd>{detail.data.submission.name}</dd></div>}
          {detail.data.submission.contact && <div><dt>Communication way</dt><dd dir="auto">{detail.data.submission.contact}</dd></div>}
        </dl>
      )}

      {isVoice && item.call_id && <VoicePlayer callId={item.call_id} />}

      <h3>{isComplaint ? "Description of the complaint" : "Customer opinion"}</h3>
      <blockquote className="quote" dir="auto">
        {comment ||
          (isVoice ? (
            <span className="muted">Audio is being transcribed…</span>
          ) : (
            <span className="muted">{isComplaint ? "The text of the complaint has not been registered." : "This feedback only includes points."}</span>
          ))}
      </blockquote>
      {isVoice && item.call_id && item.comment && (
        <Link className="small" to={voicePath(isComplaint ? "complaints" : "feedback", item.call_id)}>
          Full text with timing and speaker ←
        </Link>
      )}

      <section className="analysis-box" aria-live="polite">
        <h3>
          <Icon name="spark" size={18} /> Intelligent analysis
        </h3>
        {analysis && !requeued ? (
          <>
            <div className="analysis-labels">
              <Badge prefix="Sentiment" value={analysis.sentiment} map={sentimentLabels} />
              {isComplaint && <Badge prefix="Severity" value={analysis.severity} map={severityLabels} />}
              <span className="badge">Classification: {topics.pathLabel(analysis.category, analysis.subcategory)}</span>
              {isComplaint && analysis.resolution_status && analysis.resolution_status !== "unknown" && (
                <Badge prefix="Resolution" value={analysis.resolution_status} map={resolutionLabels} />
              )}
            </div>
            <p>{analysis.summary}</p>
            {isVoice && analysis.call_reason && (
              <p className="small">
                <strong>The reason for the call:</strong> {analysis.call_reason}
              </p>
            )}
            {analysis.action_items.length > 0 && (
              <>
                <strong className="small">Suggested actions</strong>
                <ul>
                  {analysis.action_items.map((action, index) => (
                    <li key={index}>{action}</li>
                  ))}
                </ul>
              </>
            )}
            <div className="analysis-foot">
              <span>
                Confidence {fmtPercent(analysis.confidence)} · {model?.model_name}
              </span>
              {model?.model_provider === "stub" && (
                <span className="badge warn">Offline example — AI key not set</span>
              )}
            </div>
          </>
        ) : failed ? (
          <div className="error" role="alert">
            Analysis was not performed: {analysisError}
          </div>
        ) : (
          <div className="analysis-waiting">
            <span className="spinner" />
            <span>
              {isVoice && !item.comment
                ? "Transcribing and analyzing audio; It usually takes less than a minute."
                : "Analyzing; The result will be displayed automatically."}
            </span>
          </div>
        )}
        <button className="link-button" onClick={reanalyze} disabled={busy}>
          <Icon name="refresh" size={14} /> Reanalysis
        </button>
      </section>

      <ErrorBox error={error} />
      <div className="detail-actions">
        {(workCase.data?.id || (isComplaint && item.customer_id && item.status !== "closed")) && <Link className="btn primary" to={workCase.data?.id ? `/complaints/cases/${workCase.data.id}` : `/complaints/cases?feedback=${id}`}>
          {workCase.data?.id ? "Case" : "Create case"}
        </Link>}
        {isComplaint && <button disabled={busy} onClick={() => setTaskOpen(true)}>
          <Icon name="message" size={15} /> Task text for the technical team
        </button>}
      </div>
    </div>,
  );
}
