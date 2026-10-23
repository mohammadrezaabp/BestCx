import GridPanel, { GridFilters } from "../components/GridPanel";
import { Link, useSearchParams } from "react-router-dom";
import { api } from "../api";
import { Badge, Empty, ErrorBox, Loading, PageHead, Pager, useAsync, useCursorPaging } from "../components/Common";
import DataTable from "../components/DataTable";
import Icon from "../components/Icon";
import { fmtClock, fmtDateTime, label, statusLabels } from "../format";
import type { InboxKind } from "../feedback";
import { voicePath } from "../workspace";

function CallList({ q, state, direction, inbox }: { q: string; state: string; direction: string; inbox: InboxKind }) {
  const paging = useCursorPaging();
  const { data, error, loading, reload } = useAsync(
    () => api.voiceCalls({ q, state, direction, inbox, cursor: paging.cursor, limit: paging.pageSize }),
    [q, state, direction, inbox, paging.cursor, paging.pageSize],
  );
  return <GridPanel>
    <ErrorBox error={error} />
    {Boolean(error) && <button onClick={reload}>Retry</button>}
    {loading && <Loading />}
    {data?.items.length === 0 && <Empty text={q || state || direction ? "No contact was found with these filters" : "No contact has been registered in this section yet."} />}
    {Boolean(data?.items.length) && <DataTable paginate={false} aria-label="Voice and calls">
      <thead><tr><th>Call</th><th>Customer</th><th>Contact direction</th><th>Duration</th><th>Agent</th><th>Processing status</th><th>Contact time</th></tr></thead>
      <tbody>{data?.items.map(call => <tr key={call.id}>
        <td><Link to={voicePath(inbox, call.call_id)}><Icon name="voice" size={15} /> <bdi>{call.call_id}</bdi></Link></td>
        <td>{call.customer_id ? <Link to={`/customers/${encodeURIComponent(call.customer_id)}`}>{call.customer_id}</Link> : <span className="muted">Unknown</span>}</td>
        <td>{label(statusLabels, call.direction)}</td><td><bdi>{fmtClock(call.duration_ms)}</bdi></td>
        <td>{call.agent_id || "—"}</td><td><Badge value={call.lifecycle_state} /></td>
        <td className="nowrap muted">{fmtDateTime(call.started_at)}</td>
      </tr>)}</tbody>
    </DataTable>}
    {(Boolean(data?.items.length) || paging.canGoBack) && <Pager hasMore={Boolean(data?.has_more)} canGoBack={paging.canGoBack} page={paging.page} pageSize={paging.pageSize} onPageSize={paging.setPageSize}
      loading={loading} onNext={() => paging.next(data?.next_cursor)} onBack={paging.back} />}
  </GridPanel>;
}

export default function VoiceCalls({ inbox }: { inbox: InboxKind }) {
  const [params, setParams] = useSearchParams();
  const q = params.get("q") || "", state = params.get("state") || "", direction = params.get("direction") || "";
  function change(key: string, value: string) {
    const next = new URLSearchParams(params);
    value ? next.set(key, value) : next.delete(key);
    setParams(next);
  }
  return <>
    <PageHead title="Voice and calls" subtitle={inbox === "complaints" ? "Voices and calls related to complaints" : "Voice of the customer and customer experience calls"} />
    <GridFilters as="form" className="voice-filters" key={q} role="search" onSubmit={event => {
      event.preventDefault(); change("q", String(new FormData(event.currentTarget).get("q") ?? "").trim());
    }}>
      <label className="grid-filter"><span>Search in contacts</span><div className="grid-search"><Icon name="search" size={16} />
        <input name="q" defaultValue={q} placeholder="Contact ID…" /></div></label>
      <label className="grid-filter"><span>Contact direction</span><select value={direction} onChange={event => change("direction", event.target.value)}>
        <option value="">All contacts</option>{["inbound", "outbound", "internal"].map(value => <option key={value} value={value}>{label(statusLabels, value)}</option>)}
      </select></label>
      <label className="grid-filter"><span>Processing status</span><select value={state} onChange={event => change("state", event.target.value)}>
        <option value="">All situations</option>{["ENDED", "READY_FOR_TRANSCRIPTION", "TRANSCRIBED", "ANALYZED"].map(value => <option key={value} value={value}>{label(statusLabels, value)}</option>)}
      </select></label>
      <div className="toolbar filter-actions"><button type="submit" className="primary">Search</button>
        <button type="reset" className="filter-reset" onClick={() => setParams({})}>
          <Icon name="refresh" size={15} /> Erase</button></div>
    </GridFilters>
    <CallList key={`${inbox}-${q}-${state}-${direction}`} q={q} state={state} direction={direction} inbox={inbox} />
  </>;
}
