import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { useState } from "react";
import { Link } from "react-router-dom";
import { operations } from "../operationsApi";
import { PageHead, useAsync } from "../components/Common";
import { OpsField } from "../components/OperationsCommon";
import { fmtNumber } from "../format";

export default function FollowUpCustomers() {
  const [segment, setSegment] = useState("");
  const [minimum, setMinimum] = useState(1);
  const [filter, setFilter] = useState({ segment: "", minimum: 1 });
  const data = useAsync(() => operations.followUp(filter.segment, filter.minimum), [filter.segment, filter.minimum]);
  return <>
    <PageHead title="Follow-up customers" actions={<button onClick={data.reload}>Update</button>} />
    <GridFilters as="form" onSubmit={event => { event.preventDefault(); setFilter({ segment: segment.trim(), minimum }); }}>
      <OpsField label="Customer group"><input maxLength={128} value={segment} placeholder="All groups" onChange={event => setSegment(event.target.value)} /></OpsField>
      <OpsField label="Minimum priority score"><input type="number" required min={1} max={100} value={minimum} onChange={event => setMinimum(Number(event.target.value))} /></OpsField>
      <div className="ops-inline-action"><button className="primary">Apply the filter</button><button type="button" onClick={() => { setSegment(""); setMinimum(1); setFilter({ segment: "", minimum: 1 }); }}>Clear filters</button></div>
    </GridFilters>
    <GridPanel loading={data.loading} error={data.error} empty={Boolean(data.data) && !data.data?.length} emptyText="The customer in need of follow-up was not found with this condition"><DataTable aria-label="Follow-up customers"><thead><tr><th>Customer</th><th>Tracking priority from 100</th><th>Reasons for priority</th><th>Open files</th><th>Operation</th></tr></thead><tbody>
      {data.data?.map(customer => <tr key={customer.customer_id}><td><Link to={`/customers/${encodeURIComponent(customer.customer_id)}`}>{customer.display_name}</Link><small>{customer.segment}</small></td><td><span className={`badge ${customer.follow_up_score >= 60 ? "critical" : "warn"}`}>{fmtNumber(customer.follow_up_score)}</span></td><td><ul>{customer.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul></td><td>{fmtNumber(customer.open_cases)}<small>Outside the deadline: {fmtNumber(customer.overdue_cases)}</small></td><td><Link className="btn" to={`/complaints/cases?customer=${encodeURIComponent(customer.customer_id)}`}>Viewing follow-ups</Link></td></tr>)}
    </tbody></DataTable></GridPanel>
  </>;
}
