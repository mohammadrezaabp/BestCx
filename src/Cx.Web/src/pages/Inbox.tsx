import { useEffect, useState } from "react";
import { Navigate, useSearchParams } from "react-router-dom";
import { api } from "../api";
import { PageHead, useAsync } from "../components/Common";
import FeedbackGrid from "../components/FeedbackGrid";
import ComplaintForm from "../components/ComplaintForm";
import Modal from "../components/Modal";
import type { InboxKind } from "../feedback";

export default function Inbox({ inbox }: { inbox: InboxKind }) {
  const complaints = inbox === "complaints";
  const [params, setParams] = useSearchParams();
  const session = useAsync(() => api.me(), []);
  const [creating, setCreating] = useState(false);
  const [receipt, setReceipt] = useState(0);
  const [refreshKey, setRefreshKey] = useState(0);
  useEffect(() => {
    if (!complaints || params.get("create") !== "1" || !session.data?.permissions.includes("cx.admin")) return;
    setCreating(true);
    const next = new URLSearchParams(params); next.delete("create"); setParams(next, { replace: true });
  }, [complaints, params, session.data, setParams]);
  useEffect(() => {
    if (!receipt) return;
    const timers = [1000, 3000, 8000].map(delay => window.setTimeout(() => setRefreshKey(value => value + 1), delay));
    return () => timers.forEach(window.clearTimeout);
  }, [receipt]);
  if (!complaints && params.get("kind") === "complaint") {
    const next = new URLSearchParams(params);
    next.delete("kind");
    return <Navigate to={`/complaints?${next}`} replace />;
  }
  return (
    <>
      <PageHead title={complaints ? "Complaint inbox" : "Feedback inbox"}
        subtitle={complaints ? "Customer complaints and their handling status" : "Customer reviews, ratings and survey responses"}
        actions={complaints && session.data?.permissions.includes("cx.admin") && (
        <button className="primary" onClick={() => setCreating(true)}>Record customer complaint</button>
      )} />
      {receipt > 0 && <div className="notice" role="status">A customer complaint was filed and sent for analysis.</div>}
      <FeedbackGrid inbox={inbox} syncUrl refreshKey={refreshKey} />
      {creating && <Modal title="Record customer complaint" onClose={() => setCreating(false)} wide>
        <ComplaintForm onCancel={() => setCreating(false)} onSaved={() => {
          setCreating(false); setReceipt(value => value + 1); setRefreshKey(value => value + 1);
        }} />
      </Modal>}
    </>
  );
}
