import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { useState } from "react";
import { operations, type Outcome } from "../operationsApi";
import { Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { dateRange, useOperation } from "../components/OperationsCommon";
import DatePicker from "../components/DatePicker";
import { todayIso } from "../calendarDate";
import { fmtNumber, fmtPercent } from "../format";
import { useTopics } from "../hooks/useTopics";

export default function ServiceOutcomes() {
  const [end, setEnd] = useState(todayIso);
  const [start, setStart] = useState(() => { const date = new Date(`${end}T12:00:00Z`); date.setUTCDate(date.getUTCDate() - 29); return date.toISOString().slice(0, 10); });
  const [range, setRange] = useState(() => dateRange(start, end));
  const result = useAsync(() => operations.outcomes(range.from, range.to), [range.from, range.to]);
  const action = useOperation();
  const topics = useTopics();
  return <>
    <PageHead title="Service outcomes" />
    <GridFilters as="form" onSubmit={event => { event.preventDefault(); void action.run(async () => setRange(dateRange(start, end))); }}>
      <DatePicker label="Start date" required value={start} onChange={setStart} /><DatePicker label="End date" required value={end} onChange={setEnd} />
      <div className="ops-inline-action"><button className="primary">Interval applications</button></div>{action.errorBox}
    </GridFilters>
    <ErrorBox error={result.error} />
    {result.loading ? <Loading /> : result.data && <>
      <p className="small muted">The files registered in the interval; Times are in working minutes without waiting for the customer.</p>
      <div className="metric-grid ops-metrics">{[
        ["Cases", fmtNumber(result.data.summary.total)], ["Resolved", fmtNumber(result.data.summary.resolved)], ["Outside the deadline", fmtNumber(result.data.summary.overdue)], ["Reopened", fmtNumber(result.data.summary.reopened)],
        ["Answer within the deadline", ratio(result.data.summary.response_within_sla, result.data.summary.response_measured)], ["Resolve within the deadline", ratio(result.data.summary.resolution_within_sla, result.data.summary.resolution_measured)],
      ].map(([title, value]) => <div key={title} className="card metric"><span>{title}</span><strong>{value}</strong></div>)}</div>
      <OutcomeTable title="by team" items={result.data.teams} />
      <OutcomeTable title="By topic" items={result.data.topics.map(row => ({ ...row, name: topics.label(row.name) }))} />
    </>}
  </>;
}
const ratio = (value: number, count: number) => count ? fmtPercent(value / count) : "—";
function OutcomeTable({ title, items }: { title: string; items: Outcome[] }) {
  return <GridPanel><h2>{title}</h2>{!items.length ? <Empty text="No case has been registered in this period" /> : <DataTable><thead><tr><th>Name</th><th>Case</th><th>Resolved</th><th>Average response</th><th>Average solution</th><th>Answer within the deadline</th><th>Resolve within the deadline</th></tr></thead><tbody>
    {items.map(item => <tr key={item.name}><td>{item.name}</td><td>{fmtNumber(item.total)}</td><td>{fmtNumber(item.resolved)}</td><td>{fmtNumber(item.average_response_minutes)}</td><td>{fmtNumber(item.average_resolution_minutes)}</td><td>{ratio(item.response_within_sla, item.response_measured)}</td><td>{ratio(item.resolution_within_sla, item.resolution_measured)}</td></tr>)}
  </tbody></DataTable>}</GridPanel>;
}
