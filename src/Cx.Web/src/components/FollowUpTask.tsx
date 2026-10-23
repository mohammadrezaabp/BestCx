import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, type FollowUpTaskDraft } from "../api";
import { ErrorBox, Loading } from "./Common";
import Icon from "./Icon";
import Modal from "./Modal";

/**
 * Drafting costs a model call, so a request already in flight for the same feedback is
 * shared instead of repeated (StrictMode's double effect, a drawer re-render).
 */
const inflight = new Map<string, Promise<FollowUpTaskDraft>>();

function requestDraft(key: string, feedbackId: string): Promise<FollowUpTaskDraft> {
  let pending = inflight.get(key);
  if (!pending) {
    pending = api.followUpTask(feedbackId).finally(() => inflight.delete(key));
    inflight.set(key, pending);
  }
  return pending;
}

/**
 * A draft task for the technical team, written by AI from the feedback and the customer's
 * other recent problems. The operator edits it here and copies it into the team's tracker.
 */
export default function FollowUpTask({ feedbackId, onClose }: { feedbackId: string; onClose: () => void }) {
  const [draft, setDraft] = useState<FollowUpTaskDraft | null>(null);
  const [text, setText] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [copied, setCopied] = useState(false);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setCopied(false);
    requestDraft(`${feedbackId}:${nonce}`, feedbackId)
      .then((result) => {
        if (cancelled) return;
        setDraft(result);
        setText(result.text);
      })
      .catch((err) => {
        if (!cancelled) setError(err);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [feedbackId, nonce]);

  async function copy() {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Clipboard API needs a secure context; fall back to selecting the text.
      const area = document.querySelector<HTMLTextAreaElement>(".follow-up-text");
      area?.select();
      document.execCommand("copy");
    }
    setCopied(true);
  }

  return (
    <Modal title="Suggested task for the technical team" onClose={onClose} wide>
      <ErrorBox error={error} />
      {loading && (
        <div className="analysis-waiting">
          <span className="spinner" />
          <span>Writing task text…</span>
        </div>
      )}
      {!loading && !draft && !error && <Loading />}
      {!loading && draft && (
        <>
          <div className="follow-up-meta">
            <span className={`badge ${draft.priority === "Urgent" ? "critical" : draft.priority === "High" ? "high" : ""}`}>
              Priority: {draft.priority}
            </span>
          </div>
          <label className="field">
            <span>Task text</span>
            <textarea
              className="follow-up-text"
              rows={18}
              value={text}
              onChange={(e) => {
                setText(e.target.value);
                setCopied(false);
              }}
            />
          </label>
        </>
      )}
      <div className="form-submit">
        {!loading && text && <Link className="btn primary" to={`/complaints/cases?feedback=${feedbackId}`} state={{ followUpText: text }}>Save to case</Link>}
        <button className="primary" onClick={copy} disabled={loading || !text}>
          <Icon name="check" size={16} /> {copied ? "Copied" : "Text copy"}
        </button>
        <button onClick={() => setNonce((n) => n + 1)} disabled={loading}>
          <Icon name="refresh" size={14} /> Write again
        </button>
        <button onClick={onClose}>Close</button>
      </div>
    </Modal>
  );
}
