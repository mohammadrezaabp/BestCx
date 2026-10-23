import GridPanel, { GridFilters } from "../components/GridPanel";
import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { operations } from "../operationsApi";
import { Badge, Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import DataTable from "../components/DataTable";
import Pagination from "../components/Pagination";
import { useOperation } from "../components/OperationsCommon";
import { fmtDateTime } from "../format";

export default function Notifications() {
  const action = useOperation();
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const data = useAsync(() => operations.notificationGrid({ q: q.trim(), status, page, pageSize }), [q.trim(), status, page, pageSize]);
  const items = data.data?.items ?? [];
  useEffect(() => {
    if (data.data && page > Math.max(1, Math.ceil(data.data.total / pageSize))) setPage(Math.max(1, Math.ceil(data.data.total / pageSize)));
  }, [data.data, page, pageSize]);
  async function markRead(id: string) {
    await operations.readNotification(id);
    window.dispatchEvent(new Event("notifications-changed"));
    data.reload();
  }
  return <>
    <PageHead title="Notifications" actions={<button disabled={data.loading} onClick={data.reload}>Update</button>} />
    <GridFilters>
      <label className="grid-filter"><span>Search in notifications</span><input maxLength={200} value={q} onChange={event => { setQ(event.target.value); setPage(1); }} placeholder="Title or text of notification…" /></label>
      <label className="grid-filter"><span>Status</span><select value={status} onChange={event => { setStatus(event.target.value); setPage(1); }}><option value="">All notices</option><option value="unread">Unread</option><option value="read">Read</option></select></label>
      <button onClick={() => { setQ(""); setStatus(""); setPage(1); }}>Clear filters</button>
    </GridFilters>
    <ErrorBox error={data.error} />{action.errorBox}
    <GridPanel>
      {data.loading ? <Loading /> : data.data && (!items.length ? <Empty text={q || status ? "No notification was found with these filters" : "You have no notification"} /> :
        <DataTable paginate={false} aria-label="Notifications"><thead><tr><th>Notice</th><th>Status</th><th>Time</th><th>Operation</th></tr></thead>
          <tbody>{items.map(item => <tr className={item.read_at ? "" : "unread"} key={item.id}>
            <td className="notification-content"><strong>{item.title}</strong><p title={item.body}>{item.body}</p></td>
            <td><Badge value={item.read_at ? "read" : "unread"} map={{ read: "Read", unread: "Unread" }} /></td>
            <td className="nowrap muted">{fmtDateTime(item.created_at)}</td>
            <td><div className="grid-row-actions"><Link to={item.link} onClick={() => { if (!item.read_at) void action.run(() => markRead(item.id)); }}>Observation</Link>{!item.read_at && <button disabled={action.busy} onClick={() => void action.run(() => markRead(item.id))}>It was read</button>}</div></td>
          </tr>)}</tbody>
        </DataTable>)}
      {data.data && <Pagination page={data.data.page} pageSize={data.data.page_size} total={data.data.total} onPage={setPage}
        onPageSize={size => { setPageSize(size); setPage(1); }} />}
    </GridPanel>
  </>;
}
