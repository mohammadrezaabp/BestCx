import GridPanel, { GridFilters } from "../components/GridPanel";
import { useState } from "react";
import { operations, type Problem } from "../operationsApi";
import { Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { OpsField, OpsSelect, useOperation } from "../components/OperationsCommon";
import Modal from "../components/Modal";
import DataTable from "../components/DataTable";
import TopicSelects, { rootTopicCode } from "../components/TopicSelects";
import { useTopics } from "../hooks/useTopics";
import { fmtDateTime, fmtNumber } from "../format";

const statuses = { open: "Open", investigating: "Investigating the cause", fixed: "Modified", closed: "Closed" };
export default function Problems({ manager }: { manager: boolean }) {
  const data = useAsync(operations.problems, []);
  const directory = useAsync(operations.directory, []);
  const topics = useTopics();
  const [editing, setEditing] = useState<Partial<Problem> | null>(null);
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("");
  const items = data.data?.filter(problem => (!status || problem.status === status) && `${problem.title} ${problem.root_cause} ${problem.improvement}`.includes(q.trim())) ?? [];
  const action = useOperation();
  const root = rootTopicCode(topics.options, editing?.topic ?? "") ?? editing?.topic ?? "";
  return <>
    <PageHead title="Common problems and corrective actions" actions={manager && <button className="primary" onClick={() => setEditing({ id: crypto.randomUUID(), title: "", topic: null, root_cause: "", improvement: "", status: "open", owner_id: null, version: 0 })}>New common problem</button>} />
    <ErrorBox error={data.error || directory.error} />
    <GridFilters><label className="grid-filter"><span>Search in problems</span><input value={q} onChange={event => setQ(event.target.value)} placeholder="title, cause or corrective action…" /></label>
      <OpsSelect label="Status" value={status} options={statuses} empty="All situations" onChange={setStatus} /><button onClick={() => { setQ(""); setStatus(""); }}>Clear filters</button></GridFilters>
    <GridPanel>{data.loading ? <Loading /> : data.data && (!items.length ? <Empty text="No problem was found with this condition" /> : <DataTable aria-label="Shared problems">
      <thead><tr><th>Problem</th><th>Topic</th><th>Status</th><th>Responsible for correction</th><th>Customers</th><th>Cases</th>{manager && <th>Operation</th>}</tr></thead><tbody>
        {items.map(problem => <tr key={problem.id}><td><strong>{problem.title}</strong><details><summary>Cause and corrective action</summary><dl><dt>The main cause</dt><dd className="ops-prewrap">{problem.root_cause || "Not yet confirmed"}</dd><dt>Corrective action</dt><dd className="ops-prewrap">{problem.improvement || "Not yet defined"}</dd></dl>
          {problem.fixed_at && <p className="small">Correction: {fmtDateTime(problem.fixed_at)}{problem.comparison_days > 0 && <><br />The complaint in {fmtNumber(problem.comparison_days)} Equal day: Before {fmtNumber(problem.before_count)} · Next {fmtNumber(problem.after_count)}</>}</p>}</details></td>
          <td>{topics.label(problem.topic)}</td><td><span className="badge">{statuses[problem.status as keyof typeof statuses]}</span></td><td>{directory.data?.staff.find(staff => staff.id === problem.owner_id)?.display_name || "Unassigned"}</td><td>{fmtNumber(problem.affected_customers)}</td><td>{fmtNumber(problem.case_count)}</td>
          {manager && <td><button onClick={() => setEditing(problem)}>Edit</button></td>}</tr>)}
      </tbody></DataTable>)}</GridPanel>
    {editing && <Modal title="Common problem" onClose={() => setEditing(null)} wide><form onSubmit={event => { event.preventDefault(); void action.run(async () => { await operations.saveProblem(editing); setEditing(null); data.reload(); }); }}><fieldset className="ops-form-grid" disabled={action.busy}>
      <OpsField label="Title of the problem" wide><input required maxLength={256} value={editing.title} onChange={event => setEditing({ ...editing, title: event.target.value })} /></OpsField>
      <TopicSelects options={topics.options} parentValue={root} childValue={root !== editing.topic ? editing.topic ?? "" : ""} onParentChange={value => setEditing({ ...editing, topic: value || null })} onChildChange={value => setEditing({ ...editing, topic: value || root || null })} />
      <OpsSelect label="Problem status" value={editing.status ?? "open"} options={statuses} onChange={value => setEditing({ ...editing, status: value })} />
      <OpsSelect label="Responsible for correction" value={editing.owner_id ?? ""} empty="Unassigned" options={Object.fromEntries((directory.data?.staff ?? []).filter(staff => staff.enabled && staff.role !== "data_ai_operator").map(staff => [staff.id, staff.display_name]))} onChange={value => setEditing({ ...editing, owner_id: value || null })} />
      <OpsField label="Confirmed root cause" wide><textarea maxLength={12000} required={editing.status === "fixed" || editing.status === "closed"} rows={4} value={editing.root_cause} onChange={event => setEditing({ ...editing, root_cause: event.target.value })} /></OpsField>
      <OpsField label="Corrective action" wide><textarea maxLength={12000} required={editing.status === "fixed" || editing.status === "closed"} rows={4} value={editing.improvement} onChange={event => setEditing({ ...editing, improvement: event.target.value })} /></OpsField>
    </fieldset>{action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Save the problem</button><button type="button" onClick={() => setEditing(null)}>Cancel</button></div></form></Modal>}
  </>;
}
