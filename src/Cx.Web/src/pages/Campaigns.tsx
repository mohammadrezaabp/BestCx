import GridPanel, { GridFilters } from "../components/GridPanel";
import { useState } from "react";
import { operations, type Campaign } from "../operationsApi";
import { Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { OpsField, OpsSelect, useOperation } from "../components/OperationsCommon";
import CustomerSelect from "../components/CustomerSelect";
import Modal from "../components/Modal";
import DataTable from "../components/DataTable";
import { fmtNumber, fmtPercent } from "../format";

const metrics = { csat: "Satisfaction · CSAT", ces: "Effort · CES", nps: "Recommendation · NPS" };
const triggers = { manual: "Manual invitation", case_resolved: "After the case is resolved", event: "After the event" };
export default function Campaigns() {
  const data = useAsync(operations.campaigns, []);
  const [editing, setEditing] = useState<Partial<Campaign> | null>(null);
  const [dispatch, setDispatch] = useState<Campaign | null>(null);
  const [customer, setCustomer] = useState("");
  const [selected, setSelected] = useState<string[]>([]);
  const [notice, setNotice] = useState("");
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("");
  const items = data.data?.filter(campaign => (!status || campaign.enabled === (status === "enabled")) && `${campaign.name} ${campaign.question}`.includes(q.trim())) ?? [];
  const action = useOperation();
  return <>
    <PageHead title="Survey campaigns" actions={<button className="primary" onClick={() => setEditing({ id: crypto.randomUUID(), name: "", question: "", metric: "csat", trigger: "manual", event_name: null, segment: null, cooldown_days: 30, reminder_days: 3, expires_days: 14, enabled: false })}>New campaign</button>} />
    <ErrorBox error={data.error} />{notice && <div className="notice" role="status">{notice}</div>}
    <GridFilters><label className="grid-filter"><span>Search in campaigns</span><input value={q} onChange={event => setQ(event.target.value)} placeholder="Name or survey question…" /></label>
      <OpsSelect label="Status" value={status} options={{ enabled: "Enabled", disabled: "Disabled" }} empty="All situations" onChange={setStatus} /><button onClick={() => { setQ(""); setStatus(""); }}>Clear filters</button></GridFilters>
    <GridPanel>{data.loading ? <Loading /> : data.data && (!items.length ? <Empty text="No campaign was found with this condition" /> : <DataTable aria-label="Survey campaigns">
      <thead><tr><th>Campaign</th><th>Index</th><th>Time of invitation</th><th>Status</th><th>Invitation / Answer</th><th>Response rate</th><th>Operation</th></tr></thead><tbody>{items.map(campaign => <tr key={campaign.id}>
        <td><strong>{campaign.name}</strong><details><summary>Survey details</summary><p>{campaign.question}</p>{campaign.segment && <p>Group: {campaign.segment}</p>}<p className="small muted">Expiration: {fmtNumber(campaign.expired)} · Cancel: {fmtNumber(campaign.declined)} · Avg: {fmtNumber(campaign.average_score)}</p></details></td>
        <td>{metrics[campaign.metric as keyof typeof metrics]}</td><td>{triggers[campaign.trigger as keyof typeof triggers]}</td><td><span className={`badge ${campaign.enabled ? "ok" : ""}`}>{campaign.enabled ? "Enabled" : "Disabled"}</span></td>
        <td>{fmtNumber(campaign.invitations)} / {fmtNumber(campaign.responses)}</td><td>{campaign.invitations ? fmtPercent(campaign.responses / campaign.invitations) : "—"}</td>
        <td><div className="grid-row-actions"><button onClick={() => setEditing(campaign)}>Edit</button><button disabled={!campaign.enabled} onClick={() => { setDispatch(campaign); setCustomer(""); setSelected([]); }}>Invite customers</button></div></td>
      </tr>)}</tbody></DataTable>)}</GridPanel>
    {editing && <Modal title="Setting up a survey campaign" onClose={() => setEditing(null)} wide><form onSubmit={event => { event.preventDefault(); void action.run(async () => { await operations.saveCampaign(editing); setEditing(null); data.reload(); }); }}><fieldset className="ops-form-grid" disabled={action.busy}>
      <OpsField label="Name of the campaign" wide><input required maxLength={128} value={editing.name} onChange={event => setEditing({ ...editing, name: event.target.value })} /></OpsField>
      <OpsField label="Question text for the customer" wide><textarea required maxLength={512} rows={3} value={editing.question} onChange={event => setEditing({ ...editing, question: event.target.value })} /></OpsField>
      <OpsSelect label="Index" value={editing.metric ?? "csat"} options={metrics} onChange={value => setEditing({ ...editing, metric: value })} />
      <OpsSelect label="Time of invitation" value={editing.trigger ?? "manual"} options={triggers} onChange={value => setEditing({ ...editing, trigger: value })} />
      {editing.trigger === "event" && <OpsField label="Event name"><input required maxLength={128} placeholder="For example purchase.completed" value={editing.event_name ?? ""} onChange={event => setEditing({ ...editing, event_name: event.target.value || null })} /></OpsField>}
      <OpsField label="Customer group (Optional)"><input maxLength={128} value={editing.segment ?? ""} onChange={event => setEditing({ ...editing, segment: event.target.value || null })} /></OpsField>
      <OpsField label="The interval of the invitations of this campaign (Day)"><input type="number" required min={1} max={365} value={editing.cooldown_days} onChange={event => setEditing({ ...editing, cooldown_days: Number(event.target.value) })} /></OpsField>
      <OpsField label="Reminder after (Day)"><input type="number" required min={1} max={30} value={editing.reminder_days} onChange={event => setEditing({ ...editing, reminder_days: Number(event.target.value) })} /></OpsField>
      <OpsField label="Response deadline (Day)"><input type="number" required min={(editing.reminder_days ?? 0) + 1} max={90} value={editing.expires_days} onChange={event => setEditing({ ...editing, expires_days: Number(event.target.value) })} /></OpsField>
      <label className="ops-check"><input type="checkbox" checked={editing.enabled} onChange={event => setEditing({ ...editing, enabled: event.target.checked })} />Active campaign</label>
    </fieldset>{action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Save the campaign</button><button type="button" onClick={() => setEditing(null)}>Cancel</button></div></form></Modal>}
    {dispatch && <Modal title={`Invite customers · ${dispatch.name}`} onClose={() => setDispatch(null)}><form onSubmit={event => { event.preventDefault(); void action.run(async () => {
      const ids = [...new Set([...selected, ...(customer ? [customer] : [])])];
      const result = await operations.dispatch(dispatch.id, ids);
      setNotice(`${fmtNumber(result.invited)} The invitation was registered in the portal; ${fmtNumber(result.skipped)} The customer was not invited due to the conditions of the campaign or the posting interval.`);
      setDispatch(null); data.reload();
    }); }}><CustomerSelect value={customer} onChange={setCustomer} />
      <div className="form-submit"><button type="button" disabled={!customer || selected.includes(customer) || selected.length >= 200} onClick={() => { setSelected([...selected, customer]); setCustomer(""); }}>Add to list</button></div>
      <ul className="ops-recipient-list">{selected.map(id => <li key={id}>{id}<button type="button" aria-label={`Customer removal ${id}`} onClick={() => setSelected(selected.filter(value => value !== id))}>×</button></li>)}</ul>
      {action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy || (!selected.length && !customer)}>Registration of invitations</button><button type="button" onClick={() => setDispatch(null)}>Cancel</button></div>
    </form></Modal>}
  </>;
}
