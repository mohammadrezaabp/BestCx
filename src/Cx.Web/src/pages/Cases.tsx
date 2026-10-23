import GridPanel, { GridFilters } from "../components/GridPanel";
import { useEffect, useId, useState } from "react";
import { Link, useLocation, useNavigate, useSearchParams } from "react-router-dom";
import { api } from "../api";
import { operations } from "../operationsApi";
import { Badge, Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { caseStatuses, OpsField, OpsSelect } from "../components/OperationsCommon";
import CustomerSelect from "../components/CustomerSelect";
import DataTable from "../components/DataTable";
import Pagination from "../components/Pagination";
import Modal from "../components/Modal";
import Icon from "../components/Icon";
import { fmtDateTime, fmtNumber, severityLabels } from "../format";

export default function Cases({ canRecordComplaint }: { canRecordComplaint: boolean }) {
  const [params, setParams] = useSearchParams();
  const [q, setQ] = useState(params.get("q") ?? "");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const moreFiltersId = useId();
  const moreFilterCount = ["team", "mine", "overdue"].filter(key => params.get(key)).length;
  const hasFilters = Boolean(q || ["customer", "status", "team", "mine", "overdue"].some(key => params.get(key)));
  const location = useLocation();
  const directory = useAsync(operations.directory, []);
  const key = params.toString();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [10, 20, 50].includes(Number(params.get("size"))) ? Number(params.get("size")) : 20;
  const cases = useAsync(() => operations.cases({ q: params.get("q"), customerId: params.get("customer"), status: params.get("status"),
    teamId: params.get("team"), mine: params.get("mine") === "1", overdue: params.get("overdue") === "1", page, pageSize }), [key], { keepDataOnReload: true });
  useEffect(() => { setQ(params.get("q") ?? ""); }, [params.get("q")]);
  useEffect(() => {
    const timer = window.setInterval(() => { if (!document.hidden) cases.reload(); }, 10000);
    return () => window.clearInterval(timer);
  }, [cases.reload]);
  function filter(name: string, value: string) {
    const next = new URLSearchParams(params); if (value) next.set(name, value); else next.delete(name);
    if (name !== "page") next.delete("page"); setParams(next);
  }
  function close() { const next = new URLSearchParams(params); next.delete("feedback"); setParams(next, { replace: true }); }
  return <>
    <PageHead title="Cases" actions={canRecordComplaint && <Link className="btn primary" to="/complaints?create=1">Record customer complaint</Link>} />
    {(location.state as { notice?: string } | null)?.notice && <div className="notice" role="status">{(location.state as { notice: string }).notice}</div>}
    <GridFilters as="form" className="case-filters" role="search" onSubmit={event => { event.preventDefault(); filter("q", q.trim()); }}>
      <div className="filter-primary-row">
        <OpsField label="Search in files"><div className="grid-search"><Icon name="search" size={16} />
          <input value={q} onChange={event => setQ(event.target.value)} placeholder="Title or tracking code…" /></div></OpsField>
        <CustomerSelect value={params.get("customer") ?? ""} onChange={value => filter("customer", value)} />
        <OpsSelect label="Status of the file" value={params.get("status") ?? ""} options={caseStatuses} empty="All situations" onChange={value => filter("status", value)} />
        <div className="toolbar filter-actions">
          <button type="submit" className="primary">Search</button>
          <button type="button" className={`filter-toggle${filtersOpen || moreFilterCount ? " active" : ""}`}
            aria-expanded={filtersOpen} aria-controls={moreFiltersId} onClick={() => setFiltersOpen(open => !open)}>
            <Icon name="filter" size={15} /> Filters
            {moreFilterCount > 0 && <span className="count">{fmtNumber(moreFilterCount)}</span>}
          </button>
          <button type="button" className="filter-reset" disabled={!hasFilters} onClick={() => { setQ(""); setParams({}); }}>
            <Icon name="refresh" size={15} /> Erase</button>
        </div>
      </div>
      <div className="filter-details" id={moreFiltersId} hidden={!filtersOpen}>
        <div className="case-extra-filters">
        <OpsSelect label="Team" value={params.get("team") ?? ""} options={Object.fromEntries((directory.data?.teams ?? []).map(team => [team.id, team.name]))} empty="All teams" onChange={value => filter("team", value)} />
        <div className="filter-checks">
        <label className="ops-check"><input type="checkbox" checked={params.get("mine") === "1"} onChange={event => filter("mine", event.target.checked ? "1" : "")} />My works</label>
        <label className="ops-check"><input type="checkbox" checked={params.get("overdue") === "1"} onChange={event => filter("overdue", event.target.checked ? "1" : "")} />Outside the deadline</label>
        </div>
        </div>
      </div>
    </GridFilters>
    <ErrorBox error={cases.error || directory.error} />
    <GridPanel loading={cases.loading && !cases.data} error={cases.error} className={` ${cases.loading ? " is-refreshing" : ""}`} aria-busy={cases.loading}>
      {cases.data && <>{!cases.data.items.length ? <Empty text="No file was found with these filters" /> : <DataTable paginate={false} aria-label="Cases"><thead><tr><th>Case</th><th>Customer</th><th>Status</th><th>Severity</th><th>Responsible</th><th>Resolution deadline</th></tr></thead><tbody>
        {cases.data.items.map(item => <tr key={item.id}><td><Link to={`/complaints/cases/${item.id}`}>{item.title}</Link><small>{item.reference}</small></td>
          <td><Link to={`/customers/${encodeURIComponent(item.customer_id)}`}>{item.customer_name}</Link></td><td><Badge value={item.status} map={caseStatuses} /></td>
          <td><Badge value={item.priority} map={severityLabels} /></td><td>{item.assignee_name || "Unassigned"}<small>{item.team_name}</small></td>
          <td>{item.pause_started_at ? <span className="badge warn">Waiting for the customer</span> : fmtDateTime(item.resolution_due_at)}{(item.response_overdue || item.resolution_overdue) && <small className="danger">Outside the deadline</small>}</td></tr>)}
      </tbody></DataTable>}
      <Pagination page={cases.data.page} pageSize={cases.data.page_size} total={cases.data.total} onPage={value => filter("page", String(value))} onPageSize={size => filter("size", String(size))} />
    </>}</GridPanel>
    {params.get("feedback") && <ComplaintCase key={params.get("feedback")} feedbackId={params.get("feedback")!} draftText={(location.state as { followUpText?: string } | null)?.followUpText} onClose={close} />}
  </>;
}

function ComplaintCase({ feedbackId, draftText, onClose }: { feedbackId: string; draftText?: string; onClose: () => void }) {
  const navigate = useNavigate();
  const source = useAsync(() => api.feedbackDetail(feedbackId), [feedbackId]);
  const existing = useAsync(() => operations.forFeedback(feedbackId), [feedbackId]);
  const [waitExpired, setWaitExpired] = useState(false);
  const [retry, setRetry] = useState(0);
  const feedback = source.data?.feedback;
  useEffect(() => {
    if (existing.data?.id) navigate(`/complaints/cases/${existing.data.id}`, { replace: true, state: draftText ? { followUpText: draftText } : null });
  }, [existing.data?.id, draftText, navigate]);
  useEffect(() => {
    if (feedback?.feedback_type !== "complaint" || !feedback.customer_id || feedback.status === "closed" || existing.data?.id) return;
    setWaitExpired(false);
    const timer = window.setInterval(existing.reload, 3000);
    const timeout = window.setTimeout(() => { window.clearInterval(timer); setWaitExpired(true); }, 120000);
    return () => { window.clearInterval(timer); window.clearTimeout(timeout); };
  }, [feedback?.feedback_type, feedback?.customer_id, feedback?.status, Boolean(existing.data?.id), existing.reload, retry]);
  return <Modal title="Complaint case" onClose={onClose}>
    <ErrorBox error={source.error || existing.error} />
    {source.loading ? <Loading /> : feedback && (feedback.feedback_type !== "complaint" ? <p>Feedback does not need to be processed. Its previous files are still visible.</p>
      : !feedback.customer_id ? <p role="alert">To file a case, the client of the complaint must be known.</p>
      : feedback.status === "closed" && !existing.data?.id ? <p>This complaint has already been closed and there is no follow-up file.</p>
      : waitExpired ? <p role="alert">The filing has not yet been completed. Check the processing status or try again.</p>
      : <><Loading /><p>Filing of complaint information…</p></>)}
    <div className="form-submit">{Boolean(waitExpired || source.error || existing.error) && <button onClick={() => { source.reload(); existing.reload(); setRetry(value => value + 1); }}>Retry</button>}<button onClick={onClose}>Close</button></div>
  </Modal>;
}
