import { useState } from "react";
import { Link, useLocation, useParams } from "react-router-dom";
import { operations, type Activity, type Directory, type WorkCase } from "../operationsApi";
import { Badge, Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { AssignmentFields, caseStatuses, OpsField, OpsSelect, PrioritySelect, useOperation } from "../components/OperationsCommon";
import { fmtDateTime, fmtNumber, severityLabels } from "../format";
import { useTopics } from "../hooks/useTopics";
import { feedbackComment } from "../feedback";
import CaseWorkflow from "../components/CaseWorkflow";
import Modal from "../components/Modal";

const activityKinds: Record<string, string> = { created: "File registration", message: "Expert answer", customer_message: "Customer response", updated: "Changing specifications", workflow: "Step change", status: "Status change", attachment: "Attachment", escalation: "Reference to supervisor" };
const isComment = (activity: Activity) => ["message", "customer_message"].includes(activity.kind);

export default function CaseDetail({ canManage, manager }: { canManage: boolean; manager: boolean }) {
  const { caseId = "" } = useParams();
  return <CaseWorkspace key={caseId} caseId={caseId} canManage={canManage} manager={manager} />;
}

function CaseWorkspace({ caseId, canManage, manager }: { caseId: string; canManage: boolean; manager: boolean }) {
  const location = useLocation();
  const followUpText = (location.state as { followUpText?: string } | null)?.followUpText;
  const detail = useAsync(() => operations.detail(caseId), [caseId], { keepDataOnReload: true });
  const directory = useAsync(operations.directory, []);
  const topics = useTopics();
  const action = useOperation();
  const [message, setMessage] = useState(followUpText ?? "");
  const [messageId, setMessageId] = useState(() => crypto.randomUUID());
  const [isPublic, setPublic] = useState(!followUpText);
  const [file, setFile] = useState<File | null>(null);
  const [fileId, setFileId] = useState(() => crypto.randomUUID());
  const [fileInputKey, setFileInputKey] = useState(0);
  const [tab, setTab] = useState("all");
  const [editing, setEditing] = useState(false);
  const [notice, setNotice] = useState("");
  if (detail.loading && !detail.data) return <Loading />;
  if (!detail.data) return <><ErrorBox error={detail.error} /><button onClick={detail.reload}>Retry</button></>;
  const item = detail.data.case;
  const open = item.status !== "resolved" && item.status !== "closed";
  const activities = detail.data.activities.filter(activity => tab === "all" || (tab === "comments" ? isComment(activity) : !isComment(activity)));
  const ordered = [...activities].sort((a, b) => a.created_at.localeCompare(b.created_at) || a.id.localeCompare(b.id));
  return <>
    <nav className="case-breadcrumb" aria-label="Case path"><Link to="/complaints">Complaints</Link><span>/</span><Link to="/complaints/cases">Cases</Link><span>/</span><bdi>{item.reference}</bdi></nav>
    <PageHead title={item.title} actions={<>
      {canManage && directory.data && <button onClick={() => setEditing(true)}>Edit specifications and status</button>}
      <button disabled={action.busy} onClick={detail.reload}>Update</button>
    </>} />
    {notice && <div className="notice" role="status">{notice}</div>}
    {action.errorBox}<ErrorBox error={detail.error || directory.error} />
    <div className="case-workspace">
      <div className="case-main">
        <section className="card"><h2>Description of the case</h2><p className="ops-prewrap">{feedbackComment(item.description) || item.description}</p>
          {item.resolution_text && <div className="case-resolution"><h3>Service outcomes</h3><p className="ops-prewrap">{item.resolution_text}</p></div>}
          {detail.data.feedback_ids.length > 0 && <div className="ops-linked-feedback">{detail.data.feedback_ids.map((id, index) => <Link key={id} to={`/inbox?id=${encodeURIComponent(id)}`}>View related registration {fmtNumber(index + 1)}</Link>)}</div>}
        </section>
        {detail.data.attachments.length > 0 && <section className="card"><h2>Attachments</h2><ul className="ops-attachments">{detail.data.attachments.map(attachment => <li key={attachment.id}>
          <a href={`/v1/operations/cases/${encodeURIComponent(caseId)}/attachments/${encodeURIComponent(attachment.id)}`}>{attachment.filename}</a>
          <small>{fmtNumber(Math.ceil(attachment.size_bytes / 1024))} KB · {attachment.public ? "Visible to the customer" : "Internal"} · {actorName(attachment.uploaded_by, directory.data)} · {fmtDateTime(attachment.created_at)}</small>
        </li>)}</ul></section>}
        <section className="card case-activity">
          <h2>Case activities</h2>
          {canManage && <form className="ops-message-form case-composer" onSubmit={event => { event.preventDefault(); void action.run(async () => {
            try {
              if (message.trim()) {
                await operations.message(caseId, message.trim(), isPublic, messageId);
                setMessage(""); setMessageId(crypto.randomUUID());
              }
              if (file) {
                await operations.upload(caseId, file, isPublic, fileId);
                setFile(null); setFileId(crypto.randomUUID()); setFileInputKey(value => value + 1);
              }
              setNotice("The response and attachments were recorded in the history.");
            } finally { detail.reload(); }
          }); }}>
            <fieldset className="ops-message-fields" disabled={action.busy}>
              <div className="tabs" role="group" aria-label="Response type">
                <button type="button" className={!isPublic ? "active" : ""} aria-pressed={!isPublic} onClick={() => setPublic(false)}>Internal note</button>
                <button type="button" disabled={!open} className={isPublic ? "active" : ""} aria-pressed={isPublic} onClick={() => setPublic(true)}>Answer to the customer</button>
              </div>
              <OpsField label={isPublic ? "Response text to the customer" : "Internal note text"}><textarea disabled={isPublic && !open} maxLength={12000} rows={4} value={message} placeholder="Write the action taken or your response…" onChange={event => setMessage(event.target.value)} /></OpsField>
              <OpsField label="Attachment (Maximum 10 MB)"><input key={fileInputKey} type="file" disabled={isPublic && !open} onChange={event => {
                const selected = event.target.files?.[0] ?? null;
                if (selected && selected.size > 10 * 1024 * 1024) { event.target.value = ""; setFile(null); setNotice("The size of the attachment must be a maximum of 10 MB."); return; }
                setFile(selected); setFileId(crypto.randomUUID());
              }} /></OpsField>
              <p className="small muted">{isPublic ? "This response and its attachment are displayed to the client." : "This note and its attachment are for the handling team only."}</p>
            </fieldset>
            <div className="form-submit"><button className="primary" disabled={action.busy || (!message.trim() && !file) || (isPublic && !open)}>{action.busy ? "Registering…" : isPublic ? "Send response" : "Note registration"}</button></div>
            {!open && <p className="small muted">To send a response to the client, first open the file.</p>}
          </form>}
          <div className="tabs activity-tabs" role="group" aria-label="Activity view">
            {[["all", "All activities"], ["comments", "Answers and notes"], ["history", "History of changes"]].map(([value, title]) => <button key={value} className={tab === value ? "active" : ""} aria-pressed={tab === value} onClick={() => setTab(value)}>{title}</button>)}
          </div>
          {!ordered.length ? <Empty text="There is no registered activity in this section" /> : <ol className="ops-timeline case-timeline">{ordered.map(activity => <li key={activity.id} className={isComment(activity) ? "case-comment" : "case-event"}>
            <span className="avatar" aria-hidden="true">{actorName(activity.actor, directory.data).slice(0, 1)}</span>
            <div className="case-activity-body"><div className="case-activity-meta"><strong>{actorName(activity.actor, directory.data)}</strong>
              <span>{activityKinds[activity.kind] ?? "File activity"}</span>
              {isComment(activity) && <span className={`badge ${activity.public ? "" : "warn"}`}>{activity.public ? activity.kind === "customer_message" ? "Customer response" : "Answer to the customer" : "Internal note"}</span>}
              <time>{fmtDateTime(activity.created_at)}</time>
            </div>
            {(activity.team_id || activity.stage_title) && <p className="small muted">{activity.team_id && (directory.data?.teams.find(team => team.id === activity.team_id)?.name ?? "Handling team")}{activity.team_id && activity.stage_title && " · "}{activity.stage_title}</p>}
            <p className="ops-prewrap">{activityText(activity, directory.data)}</p></div>
          </li>)}</ol>}
        </section>
      </div>
      <aside className="case-sidebar" aria-label="Specifications and proceedings">
        <section className="card"><h2>Case details</h2><dl className="case-facts">
          <div><dt>Status</dt><dd><Badge value={item.status} map={caseStatuses} /></dd></div>
          <div><dt>Severity</dt><dd><Badge value={item.priority} map={severityLabels} /></dd></div>
          <div><dt>Customer</dt><dd><Link to={`/customers/${encodeURIComponent(item.customer_id)}`}>{item.customer_name}</Link></dd></div>
          <div><dt>Assignee</dt><dd>{item.assignee_name || "Unassigned"}</dd></div>
          <div><dt>Team</dt><dd>{item.team_name || "No team"}</dd></div>
          {item.topic && <div><dt>Topic</dt><dd>{topics.label(item.topic)}</dd></div>}
          {item.problem_id && <div><dt>Common problem</dt><dd><Link to="/complaints/problems">{item.problem_title || "Viewing the problem"}</Link></dd></div>}
          <div><dt>Registration</dt><dd>{fmtDateTime(item.created_at)}</dd></div><div><dt>Last change</dt><dd>{fmtDateTime(item.updated_at)}</dd></div>
          {item.reopen_count > 0 && <div><dt>Reopen</dt><dd>{fmtNumber(item.reopen_count)} load</dd></div>}
        </dl></section>
        <CaseWorkflow key={`${caseId}:${item.version}`} item={item} directory={directory.data} manager={manager} canManage={canManage} onChanged={detail.reload} />
        <section className="card"><h2>Deadline for processing</h2><div className="ops-deadlines">
          <div><span>Initial response</span><strong>{fmtDateTime(item.response_due_at)}</strong>{item.first_response_at ? <small>Answer given: {fmtDateTime(item.first_response_at)}</small> : item.response_overdue && <small className="danger">Outside the deadline</small>}</div>
          <div><span>Problem solving</span><strong>{fmtDateTime(item.resolution_due_at)}</strong>{item.pause_started_at ? <small>Waiting for the customer’s response; The deadline is suspended</small> : item.resolution_overdue && <small className="danger">Outside the deadline</small>}</div>
        </div></section>
      </aside>
    </div>
    {editing && directory.data && <CaseEdit item={item} directory={directory.data} manager={manager} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); detail.reload(); }} />}
  </>;
}

function CaseEdit({ item, directory, manager, onClose, onSaved }: { item: WorkCase; directory: Directory; manager: boolean; onClose: () => void; onSaved: () => void }) {
  const action = useOperation();
  const [status, setStatus] = useState(item.status);
  const [team, setTeam] = useState(item.team_id ?? "");
  const [assignee, setAssignee] = useState(item.assignee_id ?? "");
  const [priority, setPriority] = useState(item.priority);
  const [resolution, setResolution] = useState(item.resolution_text);
  const [comment, setComment] = useState("");
  const closing = status === "resolved" || status === "closed";
  const reopening = ["resolved", "closed"].includes(item.status) && !closing;
  return <Modal title="Edit case" onClose={onClose}>
    {action.errorBox}<form onSubmit={event => { event.preventDefault(); void action.run(async () => {
      await operations.update(item.id, { version: item.version, status, team_id: team || null, assignee_id: assignee || null, problem_id: item.problem_id, priority, resolution_text: resolution.trim(), comment: comment.trim() });
      onSaved();
    }); }}><fieldset className="ops-form-grid" disabled={action.busy}>
      <OpsSelect label="Status of the file" value={status} options={caseStatuses} onChange={setStatus} />
      {manager ? <><PrioritySelect value={priority} onChange={setPriority} /><AssignmentFields directory={directory} team={team} assignee={assignee} onTeam={setTeam} onAssignee={setAssignee} /></> :
        <OpsSelect label="Assignee" value={assignee} options={Object.fromEntries(directory.staff.filter(staff => staff.id === item.assignee_id || staff.id === directory.current_staff_id).map(staff => [staff.id, staff.display_name || staff.username]))} empty={item.assignee_id ? undefined : "Unassigned"} onChange={setAssignee} />}
      {closing && <OpsField label="Resolution for the customer" wide><textarea required maxLength={12000} rows={4} value={resolution} onChange={event => setResolution(event.target.value)} /></OpsField>}
      <OpsField label={reopening ? "Reason for reopening" : "Explanation of the change (Optional)"} wide><textarea required={reopening} maxLength={12000} rows={3} value={comment} onChange={event => setComment(event.target.value)} /></OpsField>
    </fieldset><div className="form-submit"><button className="primary" disabled={action.busy}>Save changes</button><button type="button" disabled={action.busy} onClick={onClose}>Cancel</button></div></form>
  </Modal>;
}

function actorName(actor: string, directory: Directory | null) {
  if (actor.startsWith("customer:")) return "Customer";
  return directory?.staff.find(staff => staff.username === actor || staff.id === actor)?.display_name ||
    ({ automation: "Automatic rule", "sla-escalation": "Reference to supervisor", "complaint-auto-case": "Automatic complaint registration" } as Record<string, string>)[actor] || actor;
}
function activityText(activity: Activity, directory: Directory | null) {
  if (activity.kind !== "updated") return activity.text;
  try {
    const value = JSON.parse(activity.text) as { from: string; to: string; team: string | null; assignee: string | null; changes?: { field: string; before: string | null; after: string | null }[] };
    if (value.changes) {
      const names: Record<string, string> = { status: "Status", team: "Team", assignee: "Responsible", priority: "Severity", problem: "Common problem", resolution: "Service outcomes" };
      const display = (field: string, code: string | null) => !code ? "Not determined" : field === "status" ? caseStatuses[code] ?? code : field === "priority" ? severityLabels[code] ?? code : field === "team" ? directory?.teams.find(team => team.id === code)?.name ?? code : field === "assignee" ? directory?.staff.find(staff => staff.id === code)?.display_name ?? code : code;
      return value.changes.map(change => `${names[change.field] ?? change.field}: of «${display(change.field, change.before)}» To «${display(change.field, change.after)}»`).join("\n") || "File details saved.";
    }
    return `Status: ${caseStatuses[value.from] ?? value.from} ← ${caseStatuses[value.to] ?? value.to}\nTeam: ${directory?.teams.find(team => team.id === value.team)?.name ?? "No team"}; responsible: ${directory?.staff.find(staff => staff.id === value.assignee)?.display_name ?? "Unassigned"}`;
  } catch { return "File information updated."; }
}
