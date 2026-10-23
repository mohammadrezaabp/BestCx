import GridPanel from "../components/GridPanel";
import DataTable from "../components/DataTable";
import Pagination from "../components/Pagination";
import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api } from "../api";
import { Badge, BarList, Empty, ErrorBox, Loading, Pager, PageHead, useAsync, useCursorPaging } from "../components/Common";
import CustomerForm from "../components/CustomerForm";
import CustomerAccountForm from "../components/CustomerAccountForm";
import FeedbackGrid from "../components/FeedbackGrid";
import Icon from "../components/Icon";
import Modal from "../components/Modal";
import { fmtDateTime, fmtNumber, label, sentimentLabels, severityLabels, timelineKindLabels } from "../format";
import { useTopics } from "../hooks/useTopics";
import { operations } from "../operationsApi";
import { caseStatuses } from "../components/OperationsCommon";
import type { InboxKind } from "../feedback";

function CustomerCases({ customerId }: { customerId: string }) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const cases = useAsync(() => operations.cases({ customerId, page, pageSize }), [customerId, page, pageSize]);
  return <GridPanel title="Customer complaint files" loading={cases.loading} error={cases.error} empty={Boolean(cases.data) && !cases.data?.items.length} emptyText="Complaint case not registered"
    actions={<Link className="btn" to={`/complaints/cases?customer=${encodeURIComponent(customerId)}`}>All follow-ups</Link>}
    footer={cases.data && <Pagination page={cases.data.page} pageSize={cases.data.page_size} total={cases.data.total} onPage={setPage} onPageSize={size => { setPageSize(size); setPage(1); }} />}>
    <DataTable paginate={false} aria-label="Customer complaint files"><thead><tr><th>Case</th><th>Status</th><th>Responsible</th><th>Resolution deadline</th></tr></thead>
      <tbody>{cases.data?.items.map(item => <tr key={item.id}><td><Link to={`/complaints/cases/${item.id}`}>{item.title}</Link><small>{item.reference}</small></td><td><Badge value={item.status} map={caseStatuses} /></td><td>{item.assignee_name || "Unassigned"}</td><td>{item.pause_started_at ? "Awaiting customer reply" : fmtDateTime(item.resolution_due_at)}</td></tr>)}</tbody>
    </DataTable>
  </GridPanel>;
}
const kindFilters = ["feedback", "voice", "insight", "support", "event"];

function Timeline({ customerId }: { customerId: string }) {
  const topics = useTopics();
  const [kinds, setKinds] = useState<string[]>([]);
  const paging = useCursorPaging();
  const timeline = useAsync(
    () =>
      api.timeline(customerId, {
        kinds: kinds.length ? kinds.join(",") : undefined,
        cursor: paging.cursor,
        limit: paging.pageSize,
      }),
    [customerId, kinds.join(","), paging.cursor, paging.pageSize],
  );
  function toggleKind(kind: string) {
    paging.reset();
    setKinds((current) =>
      current.includes(kind) ? current.filter((k) => k !== kind) : [...current, kind],
    );
  }
  return (
    <details className="card timeline-card">
      <summary>
        <h2 className="section-title">Complete history of interactions</h2>
        <Icon name="chevron" size={16} />
      </summary>
      <div className="toolbar">
        {kindFilters.map((kind) => (
          <button
            key={kind}
            onClick={() => toggleKind(kind)}
            className={kinds.includes(kind) ? "primary" : ""}
            aria-pressed={kinds.includes(kind)}
          >
            {label(timelineKindLabels, kind)}
          </button>
        ))}
      </div>
      <ErrorBox error={timeline.error} />
      {timeline.loading && <Loading />}
      {timeline.data?.items.length === 0 && <Empty text="An unregistered event" />}
      {timeline.data?.items.map((item) => (
        <div className="timeline-item" key={`${item.kind}-${item.id}`}>
          <span className="small muted">{fmtDateTime(item.occurred_at)}</span>
          <span>
            <span className="badge">{label(timelineKindLabels, item.kind)}</span>
          </span>
          <div>
            <div className="timeline-title">
              {item.kind === "voice" ? (
                <Link to={`/voice/${encodeURIComponent(item.title)}`} className="mono">
                  {item.title}
                </Link>
              ) : (
                <strong className={item.kind === "event" ? "mono" : undefined}>
                  {item.kind === "insight" ? topics.label(item.title) : item.title}
                </strong>
              )}
              {item.severity && <Badge prefix="Severity" value={item.severity} map={severityLabels} />}
              {item.sentiment && <Badge prefix="sense" value={item.sentiment} map={sentimentLabels} />}
            </div>
            {item.detail && (
              <div className="small muted">
                {item.detail.length > 220 ? `${item.detail.slice(0, 220)}…` : item.detail}
              </div>
            )}
          </div>
        </div>
      ))}
      <Pager page={paging.page} pageSize={paging.pageSize} onPageSize={paging.setPageSize}
        hasMore={Boolean(timeline.data?.has_more)}
        canGoBack={paging.canGoBack}
        onNext={() => paging.next(timeline.data?.next_cursor)}
        onBack={paging.back}
      />
    </details>
  );
}

export default function Customer360() {
  const { customerId = "" } = useParams();
  const topics = useTopics();
  const [editing, setEditing] = useState(false);
  const [accountEditing, setAccountEditing] = useState(false);
  const [inbox, setInbox] = useState<InboxKind>("feedback");
  const session = useAsync(() => api.me(), []);
  const detail = useAsync(() => api.customer(customerId), [customerId]);
  const insights = useAsync(() => api.insights(customerId), [customerId]);
  const customer = detail.data?.customer;

  if (detail.loading) return <Loading />;
  if (!customer)
    return (
      <>
        <ErrorBox error={detail.error} />
        <Link to="/customers">Back to the customer list</Link>
      </>
    );

  const name = customer.display_name || customer.customer_id;

  return (
    <>
      <PageHead title={name} subtitle="Customer file" />
      <section className="card profile-card">
        <span className="avatar large">{name.slice(0, 1)}</span>
        <div className="profile-main">
          <h2>{name}</h2>
          <div className="profile-meta">
            <span className="mono">{customer.customer_id}</span>
            {customer.phone && (
              <span dir="ltr" className="mono">
                {customer.phone}
              </span>
            )}
            {customer.email && <span dir="ltr">{customer.email}</span>}
            {customer.business_id && <span>{customer.business_id}</span>}
            {customer.segment && <span className="badge">{customer.segment}</span>}
          </div>
          {customer.notes && <p className="profile-notes">{customer.notes}</p>}
        </div>
        <div className="profile-actions">
          {session.data?.permissions.includes("cx.admin") && (
            <button onClick={() => setAccountEditing(true)}>Customer portal account</button>
          )}
          <button onClick={() => setEditing(true)}>
            <Icon name="edit" size={16} /> Edit
          </button>
        </div>
      </section>

      <div className="grid cols-4 kpi-row">
        <div className="card kpi">
          <div className="label">Feedback</div>
          <div className="value">{fmtNumber(customer.feedback_count)}</div>
        </div>
        <div className="card kpi">
          <div className="label">Negative analyses</div>
          <div className="value">{fmtNumber(customer.negative_count)}</div>
        </div>
        <div className="card kpi">
          <div className="label">Current feeling</div>
          <div style={{ marginTop: 8 }}>
            <Badge value={insights.data?.current_sentiment ?? undefined} map={sentimentLabels} />
          </div>
        </div>
        <div className="card kpi">
          <div className="label">Last interaction</div>
          <div className="value small-value">{fmtDateTime(customer.last_seen_at)}</div>
        </div>
      </div>

      <div className="grid cols-2" style={{ marginBottom: 18 }}>
        <section className="card">
          <h2 className="section-title">
            <Icon name="spark" size={16} /> Smart summary
          </h2>
          {insights.loading && <Loading />}
          {insights.data?.latest_summary ? (
            <p className="insight-summary">{insights.data.latest_summary}</p>
          ) : (
            !insights.loading && <p className="muted small">No analysis has yet been registered for this client.</p>
          )}
          {insights.data && (
            <dl className="kv">
              <dt>Unsolved issues</dt>
              <dd>{fmtNumber(insights.data.unresolved_issues)}</dd>
            </dl>
          )}
        </section>
        <section className="card">
          <h2 className="section-title">Recurring topics</h2>
          {insights.data && (
            <BarList
              items={insights.data.recurring_issues.map((item) => ({
                ...item,
                name: topics.label(item.name),
              }))}
            />
          )}
        </section>
      </div>

      {session.data?.permissions.some(permission => permission === "cx.admin" || permission === "cx.work.read") && <CustomerCases customerId={customer.customer_id} />}
      <h2 className="section-heading">{inbox === "complaints" ? "This customer’s complaints" : "Feedback from this customer"}</h2>
      <div className="tabs" role="group" aria-label="Customer fund">
        <button className={inbox === "feedback" ? "active" : ""} aria-pressed={inbox === "feedback"} onClick={() => setInbox("feedback")}>Feedback</button>
        <button className={inbox === "complaints" ? "active" : ""} aria-pressed={inbox === "complaints"} onClick={() => setInbox("complaints")}>Complaints</button>
      </div>
      <FeedbackGrid key={`${customer.customer_id}-${inbox}`} inbox={inbox} customerId={customer.customer_id} />

      <Timeline customerId={customer.customer_id} />

      {editing && (
        <Modal title="Customer edit" onClose={() => setEditing(false)} wide>
          <CustomerForm
            customer={customer}
            onCancel={() => setEditing(false)}
            onSaved={() => {
              setEditing(false);
              detail.reload();
            }}
          />
        </Modal>
      )}
      {accountEditing && (
        <Modal title="Customer portal account" onClose={() => setAccountEditing(false)}>
          <CustomerAccountForm customerId={customerId}
            onCancel={() => setAccountEditing(false)} onSaved={() => setAccountEditing(false)} />
        </Modal>
      )}
    </>
  );
}
