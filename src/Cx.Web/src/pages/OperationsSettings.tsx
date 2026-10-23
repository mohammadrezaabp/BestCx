import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { useState } from "react";
import { operations, type Directory, type Policy, type Rule, type Staff, type Team, type Workflow } from "../operationsApi";
import WorkflowEditor from "../components/WorkflowEditor";
import { Badge, PageHead, useAsync } from "../components/Common";
import { AssignmentFields, OpsField, OpsSelect, PrioritySelect, staffRoles, useOperation } from "../components/OperationsCommon";
import Modal from "../components/Modal";
import DatePicker from "../components/DatePicker";
import TopicSelects, { rootTopicCode } from "../components/TopicSelects";
import { useTopics } from "../hooks/useTopics";
import { formatCalendarDate } from "../calendarDate";
import { channelLabels, fmtDateTime, fmtNumber, severityLabels } from "../format";
import { Link } from "react-router-dom";

export default function OperationsSettings({ canManageTeam }: { canManageTeam: boolean }) {
  const [tab, setTab] = useState("sla");
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<Policy | Rule | Staff | Team | Workflow | null>(null);
  const directory = useAsync(operations.directory, []);
  const policies = useAsync(operations.policies, []);
  const rules = useAsync(operations.rules, []);
  const executions = useAsync(operations.executions, []);
  const workflows = useAsync(operations.workflows, []);
  const tabs: Record<string, string> = { sla: "Response and resolution deadline", workflows: "Workflows", rules: "Automatic rules", ...(canManageTeam ? { teams: "Teams", staff: "Agents" } : {}) };
  const matches = (name: string, enabled: boolean) => name.includes(q.trim()) && (!status || enabled === (status === "enabled"));
  const workflowRows = workflows.data?.filter(item => matches(`${item.name} ${item.description}`, item.enabled)) ?? [];
  const policyRows = policies.data?.filter(item => matches(item.name, item.enabled)) ?? [];
  const ruleRows = rules.data?.filter(item => matches(item.name, item.enabled)) ?? [];
  const teamRows = directory.data?.teams.filter(item => matches(item.name, item.enabled)) ?? [];
  const staffRows = directory.data?.staff.filter(item => matches(`${item.display_name} ${item.username}`, item.enabled)) ?? [];
  const source = tab === "sla" ? policies : tab === "workflows" ? workflows : tab === "rules" ? rules : directory;
  const count = tab === "sla" ? policyRows.length : tab === "workflows" ? workflowRows.length : tab === "rules" ? ruleRows.length : tab === "teams" ? teamRows.length : staffRows.length;
  function saved() { setEditing(null); directory.reload(); policies.reload(); rules.reload(); executions.reload(); workflows.reload(); }
  function create() {
    if (tab === "workflows") setEditing({ id: crypto.randomUUID(), name: "", description: "", topic: null, stages: [{ title: "", description: "", team_id: null }], enabled: true, version: 0 });
    if (tab === "rules") setEditing({ id: crypto.randomUUID(), name: "", topic: null, severity: null, channel: null, repeat_count: 1, priority: "medium", team_id: null, assignee_id: null, create_case: true, notify: true, enabled: false });
    if (tab === "teams" && canManageTeam) setEditing({ id: crypto.randomUUID(), name: "", lead_id: null, members: [], enabled: true });
    if (tab === "staff" && canManageTeam) setEditing({ id: crypto.randomUUID(), username: "", display_name: "", role: "cx_analyst", enabled: true });
  }
  return <>
    <PageHead title="Case settings" />
    <div className="tabs" role="group" aria-label="Case settings">{Object.entries(tabs).map(([key, label]) => <button key={key} className={tab === key ? "active" : ""} aria-pressed={tab === key} onClick={() => { setTab(key); setQ(""); setStatus(""); setEditing(null); }}>{label}</button>)}</div>
    <GridPanel title={tabs[tab]} loading={source.loading} error={source.error} empty={Boolean(source.data) && count === 0} emptyText="No case with this condition was found"
      actions={<><button onClick={saved} disabled={source.loading}>Update</button>{tab !== "sla" && <button className="primary" onClick={create}>Add {tab === "workflows" ? "Workflow" : tab === "rules" ? "Rule" : tab === "teams" ? "Team" : "Agent"}</button>}</>}
      filters={<GridFilters><OpsField label="Search"><input value={q} onChange={event => setQ(event.target.value)} placeholder="Name or title…" /></OpsField>
        <OpsSelect label="Status" value={status} empty="All situations" options={{ enabled: "Enabled", disabled: "Disabled" }} onChange={setStatus} /><button onClick={() => { setQ(""); setStatus(""); }}>Clear filters</button></GridFilters>}>
      {tab === "workflows" && <DataTable key={tab} aria-label={tabs[tab]}><thead><tr><th>Workflow</th><th>Stages and teams</th><th>Status</th><th>Operation</th></tr></thead><tbody>{workflowRows.map(workflow => <tr key={workflow.id}>
        <td>{workflow.name}<small>{workflow.description}</small></td><td>{workflow.stages.map((stage, index) => <small key={index}>{fmtNumber(index + 1)}. {stage.title} · {directory.data?.teams.find(team => team.id === stage.team_id)?.name ?? "Smart team selection"}</small>)}</td>
        <td><Badge value={workflow.enabled ? "active" : "inactive"} map={{ active: "Enabled", inactive: "Disabled" }} /></td><td><button onClick={() => setEditing(workflow)}>Edit</button></td>
      </tr>)}</tbody></DataTable>}
      {tab === "sla" && <DataTable key={tab} aria-label={tabs[tab]}><thead><tr><th>Policy</th><th>Severity</th><th>Response deadline</th><th>Resolution deadline</th><th>Status</th><th>Operation</th></tr></thead><tbody>{policyRows.map(policy => <tr key={policy.id}>
        <td>{policy.name}</td><td><Badge value={policy.priority} map={severityLabels} /></td><td>{fmtNumber(policy.response_minutes)} working minutes</td><td>{fmtNumber(policy.resolution_minutes)} working minutes</td><td>{policy.enabled ? "Enabled" : "Disabled"}</td><td><button onClick={() => setEditing(policy)}>Edit</button></td>
      </tr>)}</tbody></DataTable>}
      {tab === "rules" && <DataTable key={tab} aria-label={tabs[tab]}><thead><tr><th>Rule</th><th>Severity of the case</th><th>Repetition required</th><th>Status</th><th>Operation</th></tr></thead><tbody>{ruleRows.map(rule => <tr key={rule.id}>
        <td>{rule.name}</td><td>{severityLabels[rule.priority]}</td><td>{fmtNumber(rule.repeat_count)}</td><td>{rule.enabled ? "Enabled" : "Disabled"}</td><td><button onClick={() => setEditing(rule)}>Edit</button></td>
      </tr>)}</tbody></DataTable>}
      {tab === "teams" && canManageTeam && <DataTable key={tab} aria-label={tabs[tab]}><thead><tr><th>Team</th><th>Supervisor</th><th>Members</th><th>Status</th><th>Operation</th></tr></thead><tbody>{teamRows.map(team => <tr key={team.id}>
        <td>{team.name}<small>{team.description}</small></td><td>{directory.data?.staff.find(staff => staff.id === team.lead_id)?.display_name || "—"}</td><td>{fmtNumber(team.members.length)}</td><td>{team.enabled ? "Enabled" : "Disabled"}</td><td><button onClick={() => setEditing(team)}>Edit</button></td>
      </tr>)}</tbody></DataTable>}
      {tab === "staff" && canManageTeam && <DataTable key={tab} aria-label={tabs[tab]}><thead><tr><th>Name</th><th>Username</th><th>role</th><th>Status</th><th>Operation</th></tr></thead><tbody>{staffRows.map(staff => <tr key={staff.id}>
        <td>{staff.display_name}</td><td>{staff.username}</td><td>{staffRoles[staff.role] ?? staff.role}</td><td>{staff.enabled ? "Enabled" : "Disabled"}</td><td><button onClick={() => setEditing(staff)}>Edit</button></td>
      </tr>)}</tbody></DataTable>}
    </GridPanel>
    {tab === "rules" && <GridPanel title="The latest implementations of the rules" loading={executions.loading} error={executions.error} empty={Boolean(executions.data) && !executions.data?.length} emptyText="No implementation has been registered yet" actions={<button onClick={executions.reload}>Update</button>}>
      <DataTable aria-label="Implementations of the rules"><thead><tr><th>Rule</th><th>Result</th><th>Time</th><th>Case</th></tr></thead><tbody>{executions.data?.map(item => <tr key={`${item.rule_id}:${item.feedback_id}`}>
        <td>{item.rule_name}</td><td>{item.state === "completed" ? "Successful" : <span className="danger">{item.error || "Unsuccessful"}</span>}</td><td>{fmtDateTime(item.executed_at)}</td><td>{item.case_id ? <Link to={`/complaints/cases/${item.case_id}`}>Case</Link> : <Link to={`/complaints?id=${item.feedback_id}`}>Complaint</Link>}</td>
      </tr>)}</tbody></DataTable>
    </GridPanel>}
    {editing && tab === "sla" && <PolicyEditor value={editing as Policy} onClose={() => setEditing(null)} onSaved={saved} />}
    {editing && tab === "rules" && directory.data && <RuleEditor value={editing as Rule} directory={directory.data} onClose={() => setEditing(null)} onSaved={saved} />}
    {editing && tab === "staff" && <StaffEditor value={editing as Staff} exists={directory.data?.staff.some(staff => staff.id === editing.id) ?? false} onClose={() => setEditing(null)} onSaved={saved} />}
    {editing && tab === "teams" && directory.data && <TeamEditor value={editing as Team} directory={directory.data} onClose={() => setEditing(null)} onSaved={saved} />}
    {editing && tab === "workflows" && directory.data && <WorkflowEditor value={editing as Workflow} directory={directory.data} onClose={() => setEditing(null)} onSaved={saved} />}
  </>;
}
type EditorCallbacks = { onClose: () => void; onSaved: () => void };
function PolicyEditor({ value, onClose, onSaved }: { value: Policy } & EditorCallbacks) {
  const [draft, setDraft] = useState(value);
  const [holiday, setHoliday] = useState("");
  const action = useOperation();
  const days: [number, string][] = [[1, "Monday"], [2, "Tuesday"], [3, "Wednesday"], [4, "Thursday"], [5, "Friday"], [6, "Saturday"], [0, "Sunday"]];
  return <Modal title="Edit the processing deadline" onClose={onClose} wide><form onSubmit={event => { event.preventDefault(); void action.run(async () => { await operations.savePolicy(draft); onSaved(); }); }}>
    <fieldset className="ops-form-grid" disabled={action.busy}>
      <OpsField label="Policy name" wide><input required maxLength={128} value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })} /></OpsField>
      <OpsField label="Response deadline (working minutes)"><input type="number" required min={1} max={525600} value={draft.response_minutes} onChange={event => setDraft({ ...draft, response_minutes: Number(event.target.value) })} /></OpsField>
      <OpsField label="Resolution deadline (working minutes)"><input type="number" required min={draft.response_minutes} max={525600} value={draft.resolution_minutes} onChange={event => setDraft({ ...draft, resolution_minutes: Number(event.target.value) })} /></OpsField>
      <OpsField label="Start of working hours"><input type="time" required value={draft.calendar.starts_at} onChange={event => setDraft({ ...draft, calendar: { ...draft.calendar, starts_at: event.target.value } })} /></OpsField>
      <OpsField label="End of working hours"><input type="time" required value={draft.calendar.ends_at} onChange={event => setDraft({ ...draft, calendar: { ...draft.calendar, ends_at: event.target.value } })} /></OpsField>
      <div className="ops-wide"><span className="small muted">Working days</span><div className="ops-checks">{days.map(([day, name]) => <label key={day} className="ops-check"><input type="checkbox" checked={draft.calendar.working_days.includes(day)} onChange={event => setDraft({ ...draft, calendar: { ...draft.calendar, working_days: event.target.checked ? [...draft.calendar.working_days, day] : draft.calendar.working_days.filter(value => value !== day) } })} />{name}</label>)}</div></div>
      <DatePicker label="Holiday" value={holiday} onChange={setHoliday} />
      <div className="ops-inline-action"><button type="button" onClick={() => { if (holiday && !draft.calendar.holidays.includes(holiday)) { setDraft({ ...draft, calendar: { ...draft.calendar, holidays: [...draft.calendar.holidays, holiday].sort() } }); setHoliday(""); } }}>Add closure</button></div>
      <div className="ops-chips ops-wide">{draft.calendar.holidays.map(date => <span className="badge" key={date}>{formatCalendarDate(date)}<button type="button" aria-label={`Eliminate shutdown ${formatCalendarDate(date)}`} onClick={() => setDraft({ ...draft, calendar: { ...draft.calendar, holidays: draft.calendar.holidays.filter(value => value !== date) } })}>×</button></span>)}</div>
      <label className="ops-check ops-wide"><input type="checkbox" checked={draft.enabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })} />Active policy</label>
    </fieldset>{action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Policy storage</button><button type="button" onClick={onClose}>Cancel</button></div>
  </form></Modal>;
}
function RuleEditor({ value, directory, onClose, onSaved }: { value: Rule; directory: Directory } & EditorCallbacks) {
  const [draft, setDraft] = useState(value);
  const topics = useTopics();
  const root = rootTopicCode(topics.options, draft.topic ?? "") ?? draft.topic ?? "";
  const action = useOperation();
  return <Modal title="Automatic rule" onClose={onClose} wide><form onSubmit={event => { event.preventDefault(); void action.run(async () => { await operations.saveRule(draft); onSaved(); }); }}><fieldset className="ops-form-grid" disabled={action.busy}>
    <OpsField label="The name of the rule" wide><input required maxLength={128} value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })} /></OpsField>
    <TopicSelects options={topics.options} parentValue={root} childValue={root !== draft.topic ? draft.topic ?? "" : ""} onParentChange={value => setDraft({ ...draft, topic: value || null })} onChildChange={value => setDraft({ ...draft, topic: value || root || null })} />
    <OpsSelect label="The condition of intensity of analysis" value={draft.severity ?? ""} options={severityLabels} empty="Any intensity" onChange={value => setDraft({ ...draft, severity: value || null })} />
    <OpsSelect label="Condition of the channel" value={draft.channel ?? ""} options={channelLabels} empty="Each channel" onChange={value => setDraft({ ...draft, channel: value || null })} />
    <OpsField label="Minimum repetition in 30 days"><input type="number" required min={1} max={1000} value={draft.repeat_count} onChange={event => setDraft({ ...draft, repeat_count: Number(event.target.value) })} /></OpsField>
    <PrioritySelect value={draft.priority} onChange={value => setDraft({ ...draft, priority: value })} />
    <AssignmentFields directory={directory} team={draft.team_id ?? ""} assignee={draft.assignee_id ?? ""} onTeam={value => setDraft(current => ({ ...current, team_id: value || null }))} onAssignee={value => setDraft(current => ({ ...current, assignee_id: value || null }))} />
    <div className="ops-checks ops-wide">{([['create_case', 'Making a follow-up file'], ['notify', 'Notice to the manager and supervisor'], ['enabled', 'active rule']] as const).map(([key, title]) => <label className="ops-check" key={key}><input type="checkbox" checked={draft[key]} onChange={event => setDraft({ ...draft, [key]: event.target.checked })} />{title}</label>)}</div>
  </fieldset>{action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Save the rule</button><button type="button" onClick={onClose}>Cancel</button></div></form></Modal>;
}
function StaffEditor({ value, exists, onClose, onSaved }: { value: Staff; exists: boolean } & EditorCallbacks) {
  const [draft, setDraft] = useState({ ...value, password: "" });
  const action = useOperation();
  return <Modal title={exists ? "Expert editing" : "New expert"} onClose={onClose}><form onSubmit={event => { event.preventDefault(); void action.run(async () => { await operations.saveStaff(draft); onSaved(); }); }}><fieldset className="ops-form-grid" disabled={action.busy}>
    <OpsField label="Name"><input required maxLength={256} value={draft.display_name} onChange={event => setDraft({ ...draft, display_name: event.target.value })} /></OpsField>
    <OpsField label="Username"><input required maxLength={128} autoComplete="off" value={draft.username} onChange={event => setDraft({ ...draft, username: event.target.value })} /></OpsField>
    <OpsSelect label="role" value={draft.role} options={staffRoles} onChange={value => setDraft({ ...draft, role: value })} />
    <OpsField label={exists ? "New password (Optional)" : "Password"}><input type="password" required={!exists} minLength={8} maxLength={256} autoComplete="new-password" value={draft.password} onChange={event => setDraft({ ...draft, password: event.target.value })} /></OpsField>
    <label className="ops-check ops-wide"><input type="checkbox" checked={draft.enabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })} />Active account</label>
  </fieldset>{action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Save the expert</button><button type="button" onClick={onClose}>Cancel</button></div></form></Modal>;
}
function TeamEditor({ value, directory, onClose, onSaved }: { value: Team; directory: Directory } & EditorCallbacks) {
  const [draft, setDraft] = useState({ ...value, description: value.description ?? "" });
  const action = useOperation();
  const people = directory.staff.filter(staff => staff.enabled && staff.role !== "data_ai_operator");
  return <Modal title="Handling team" onClose={onClose}><form onSubmit={event => { event.preventDefault(); void action.run(async () => { await operations.saveTeam(draft); onSaved(); }); }}><fieldset className="ops-form-grid" disabled={action.busy}>
    <OpsField label="Name of the team" wide><input required maxLength={128} value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })} /></OpsField>
    <OpsField label="Description of team tasks for intelligent selection" wide><textarea maxLength={4000} rows={3} value={draft.description}
      placeholder="The expertise of the team, the type of problems it handles and its responsibilities…" onChange={event => setDraft({ ...draft, description: event.target.value })} /></OpsField>
    <OpsSelect label="Supervisor" value={draft.lead_id ?? ""} options={Object.fromEntries(people.map(staff => [staff.id, staff.display_name]))} empty="No supervisor" onChange={value => setDraft({ ...draft, lead_id: value || null })} />
    <label className="ops-check"><input type="checkbox" checked={draft.enabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })} />Active team</label>
    <div className="ops-wide"><span className="small muted">Team members</span><div className="ops-checks">{people.map(staff => <label className="ops-check" key={staff.id}><input type="checkbox" checked={draft.members.includes(staff.id) || draft.lead_id === staff.id} disabled={draft.lead_id === staff.id} onChange={event => setDraft({ ...draft, members: event.target.checked ? [...draft.members, staff.id] : draft.members.filter(id => id !== staff.id) })} />{staff.display_name}</label>)}</div></div>
  </fieldset>{action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Save the team</button><button type="button" onClick={onClose}>Cancel</button></div></form></Modal>;
}
