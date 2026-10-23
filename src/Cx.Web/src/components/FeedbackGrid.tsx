import GridPanel, { GridFilters } from "./GridPanel";
import DataTable from "./DataTable";
import { useEffect, useId, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, type FeedbackGridPage, type FeedbackGridParams, type FeedbackGridRow } from "../api";
import { fmtDateTime, fmtNumber, label, sentimentLabels, severityLabels, statusLabels, channelOptions, channelLabels } from "../format";
import { Badge, Empty, ErrorBox } from "./Common";
import FeedbackDetail from "./FeedbackDetail";
import Icon from "./Icon";
import Modal from "./Modal";
import Pagination from "./Pagination";
import { NpsBadge } from "./Nps";
import { useTopics } from "../hooks/useTopics";
import TopicSelects from "./TopicSelects";
import CustomerSelect from "./CustomerSelect";
import DatePicker from "./DatePicker";
import { feedbackComment, type InboxKind } from "../feedback";

type Filters = Required<Pick<FeedbackGridParams, "page" | "pageSize">> &
  Omit<FeedbackGridParams, "page" | "pageSize"> & { start?: string; end?: string };

const FILTER_KEYS = ["q", "status", "sentiment", "severity", "category", "subcategory", "kind", "analysis", "channel", "customerId", "start", "end"] as const;
const STATUS_OPTIONS = ["new", "in_progress", "closed"];

/** Filter state kept in the URL on the inbox page (shareable, survives reload), or locally when embedded. */
function useFilters(syncUrl: boolean): [Filters, (patch: Partial<Filters>) => void, string | null, (id: string | null) => void] {
  const [params, setParams] = useSearchParams();
  const [local, setLocal] = useState<Filters>({ page: 1, pageSize: 20 });
  const [localSelected, setLocalSelected] = useState<string | null>(null);

  if (!syncUrl) {
    return [
      local,
      (patch) => setLocal((current) => ({ ...current, page: 1, ...patch })),
      localSelected,
      setLocalSelected,
    ];
  }

  const filters: Filters = {
    page: Math.max(1, Number(params.get("page")) || 1),
    pageSize: Number(params.get("size")) || 20,
  };
  for (const key of FILTER_KEYS) {
    const value = params.get(key);
    if (value) filters[key] = value;
  }

  const update = (patch: Partial<Filters>) => {
    const next = new URLSearchParams(params);
    const merged: Filters = { ...filters, page: 1, ...patch };
    for (const key of FILTER_KEYS) {
      const value = merged[key];
      if (value) next.set(key, String(value));
      else next.delete(key);
    }
    if (merged.page > 1) next.set("page", String(merged.page));
    else next.delete("page");
    if (merged.pageSize !== 20) next.set("size", String(merged.pageSize));
    else next.delete("size");
    setParams(next);
  };

  const select = (id: string | null) => {
    const next = new URLSearchParams(params);
    if (id) next.set("id", id);
    else next.delete("id");
    setParams(next, { replace: true });
  };

  return [filters, update, params.get("id"), select];
}

function AnalysisCell({ row }: { row: FeedbackGridRow }) {
  const topics = useTopics();
  if (row.analysis_state === "failed")
    return (
      <span className="badge negative" title={row.analysis_error ?? undefined}>
        <Icon name="alert" size={12} /> Analysis failed
      </span>
    );
  if (row.analysis_state === "pending")
    return (
      <span className="analysis-pending">
        <span className="spinner small" /> {row.kind === "voice" && !row.comment ? "Transcription…" : "Analyzing…"}
      </span>
    );
  return (
    <div className="analysis-cell">
      <div className="analysis-cell-labels">
        <Badge prefix="Sentiment" value={row.sentiment} map={sentimentLabels} />
        {row.category && <span className="badge">Classification: {topics.pathLabel(row.category, row.subcategory)}</span>}
      </div>
      {row.summary && <p className="analysis-cell-summary">{row.summary}</p>}
    </div>
  );
}

function Stars({ value, outOf10 }: { value?: number | null; outOf10?: boolean }) {
  if (value == null) return <span className="muted">—</span>;
  if (outOf10) return <span className="mono">{fmtNumber(value)}/10</span>;
  return (
    <span className="stars" aria-label={`${value} From 5`}>
      {"★".repeat(Math.max(0, Math.min(5, value)))}
      <span className="muted">{"★".repeat(Math.max(0, 5 - value))}</span>
    </span>
  );
}

/**
 * Feedback as a data grid: compact filters, free-text search, AI-label filters and
 * numbered pages. Rows still being analysed refresh on their own.
 */
export default function FeedbackGrid({
  inbox = "feedback",
  customerId,
  syncUrl = false,
  refreshKey = 0,
}: {
  inbox?: InboxKind;
  /** Fixes the grid to one customer (Customer 360) and hides the customer column. */
  customerId?: string;
  syncUrl?: boolean;
  refreshKey?: number;
}) {
  const complaints = inbox === "complaints";
  const noun = complaints ? "Complaint" : "Feedback";
  const plural = complaints ? "Complaints" : "Feedback";
  const [rawFilters, update, selectedId, select] = useFilters(syncUrl);
  // The page owns its inbox: URL filters and clearing filters cannot mix the two lists.
  const filters = { ...rawFilters, inbox,
    status: complaints ? rawFilters.status : undefined,
    kind: ["text", "voice", ...(!complaints ? ["voc"] : [])].includes(rawFilters.kind ?? "") ? rawFilters.kind : undefined,
    severity: complaints ? rawFilters.severity : undefined,
  };
  const topics = useTopics();
  const [data, setData] = useState<FeedbackGridPage | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [notice, setNotice] = useState("");
  const [nonce, setNonce] = useState(0);
  const [search, setSearch] = useState(filters.q ?? "");
  const [dateResetKey, setDateResetKey] = useState(0);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const moreFiltersId = useId();
  const lastKey = useRef("");

  const filteredCustomerId = customerId ?? filters.customerId;
  const rangeError = [filters.start, filters.end].some(date => date && (!/^\d{4}-\d{2}-\d{2}$/.test(date) || Number.isNaN(Date.parse(date))))
    || Boolean(filters.start && filters.end && filters.start > filters.end);
  const key = JSON.stringify({ ...filters, customerId: filteredCustomerId });
  useEffect(() => setSearch(filters.q ?? ""), [filters.q]);

  useEffect(() => {
    let cancelled = false;
    if (rangeError) { setError(new Error("The time period is not valid.")); setLoading(false); return; }
    const { start, end, ...requestFilters } = filters;
    const endDay = end ? new Date(`${end}T12:00:00Z`) : null;
    if (endDay) endDay.setUTCDate(endDay.getUTCDate() + 1);
    const changed = lastKey.current !== key;
    lastKey.current = key;
    if (changed) setLoading(true);
    api
      .feedbackGrid({ ...requestFilters, customerId: filteredCustomerId,
        from: start ? new Date(`${start}T00:00:00+00:00`).toISOString() : undefined,
        // The grid API's upper bound is inclusive; include the selected day's last microsecond.
        to: endDay ? new Date(new Date(`${endDay.toISOString().slice(0, 10)}T00:00:00+00:00`).getTime() - 1000)
          .toISOString().replace(".000Z", ".999999Z") : undefined,
      })
      .then((page) => {
        if (cancelled) return;
        setData(page);
        setError(null);
      })
      .catch((err) => !cancelled && setError(err))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [key, nonce, refreshKey]);

  // Silent refresh while something on screen is still being transcribed or analysed.
  const pending = data?.items.some((row) => row.analysis_state === "pending") ?? false;
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => setNonce((n) => n + 1), 5000);
    return () => window.clearTimeout(timer);
  }, [pending, data]);

  useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(""), 4000);
    return () => window.clearTimeout(timer);
  }, [notice]);

  const activeFilters = FILTER_KEYS.filter((key) => filters[key]).length;
  const moreFilterCount = FILTER_KEYS.filter(key => key !== "q" && key !== "customerId" && filters[key]).length;
  const selectedRow = data?.items.find((row) => row.id === selectedId);
  const channels = [...new Set([...channelOptions, ...(data?.items.map(row => row.channel).filter((c): c is string => Boolean(c)) ?? []), ...(filters.channel ? [filters.channel] : [])])];

  return (
    <div className="feedback-grid">
      <GridFilters className=" inbox-filters">
        <div className="inbox-filter-top">
          <div className={`feedback-search-row${customerId ? " single" : ""}`}>
            <div className="grid-filter">
              <span>Search in {plural}</span>
              <form
                role="search"
                className="grid-search"
                onSubmit={(event) => {
                  event.preventDefault();
                  update({ q: search.trim() || undefined });
                }}
              >
                <Icon name="search" size={16} />
                <input
                  aria-label={`Search in ${plural}`}
                  placeholder={customerId ? "Search in text and summary…" : "Search in text, customer name or analysis summary…"}
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                />
                {search && (
                  <button
                    type="button"
                    className="icon-button"
                    aria-label="Clear search"
                    onClick={() => {
                      setSearch("");
                      update({ q: undefined });
                    }}
                  >
                    <Icon name="close" size={14} />
                  </button>
                )}
                <button type="submit" className="search-submit" aria-label={`Search in ${plural}`}>Search</button>
              </form>
            </div>
            {!customerId && <CustomerSelect value={filters.customerId ?? ""}
              onChange={value => update({ customerId: value || undefined })} />}
          </div>
          <div className="inbox-filter-actions">
            <button type="button" className={`filter-toggle${filtersOpen || moreFilterCount ? " active" : ""}`}
              aria-expanded={filtersOpen} aria-controls={moreFiltersId} onClick={() => setFiltersOpen(open => !open)}>
              <Icon name="filter" size={15} /> Filters
              {moreFilterCount > 0 && <span className="count">{fmtNumber(moreFilterCount)}</span>}
              <Icon name="chevron" size={14} />
            </button>
            <button
              type="button"
              className="filter-reset"
              disabled={!activeFilters && !search}
              onPointerDown={event => event.preventDefault()}
              onClick={() => {
                setSearch("");
                setDateResetKey(key => key + 1);
                update({
                  q: undefined,
                  status: undefined,
                  sentiment: undefined,
                  severity: undefined,
                  category: undefined,
                  subcategory: undefined,
                  kind: undefined,
                  analysis: undefined,
                  channel: undefined,
                  customerId: undefined,
                  start: undefined,
                  end: undefined,
                });
              }}
            >
              <Icon name="refresh" size={15} /> Clear filters
            </button>
          </div>
        </div>
        <div className="filter-details" id={moreFiltersId} hidden={!filtersOpen}>
        <div className="inbox-filter-scope">
          <DatePicker key={`start-${dateResetKey}`} label="Start date" value={filters.start ?? ""} max={filters.end}
            onChange={value => update({ start: value || undefined })} />
          <DatePicker key={`end-${dateResetKey}`} label="End date" value={filters.end ?? ""} min={filters.start}
            onChange={value => update({ end: value || undefined })} />
          <TopicSelects options={topics.options} parentValue={filters.category ?? ""} childValue={filters.subcategory ?? ""}
            onParentChange={value => update({ category: value || undefined, subcategory: undefined })}
            onChildChange={value => update({ subcategory: value || undefined })} />
        </div>
        <div className="inbox-filter-attributes">
          {complaints && <label className="grid-filter">
            <span>Status {noun}</span>
            <select aria-label={`Status ${noun}`} value={filters.status ?? ""}
              onChange={event => update({ status: event.target.value || undefined })}>
              <option value="">All situations</option>
              {STATUS_OPTIONS.map(status => <option key={status} value={status}>{label(statusLabels, status)}</option>)}
            </select>
          </label>}
          <label className="grid-filter">
            <span>Channel</span>
            <select aria-label="Channel" value={filters.channel ?? ""}
              onChange={event => update({ channel: event.target.value || undefined })}>
              <option value="">All channels</option>
              {channels.map(channel => <option key={channel} value={channel}>{label(channelLabels, channel)}</option>)}
            </select>
          </label>
          <label className="grid-filter">
            <span>Sentiment</span>
            <select
              aria-label="Sentiment"
              value={filters.sentiment ?? ""}
              onChange={(event) => update({ sentiment: event.target.value || undefined })}
            >
              <option value="">All emotions</option>
              {["negative", "mixed", "neutral", "positive"].map((value) => (
                <option key={value} value={value}>
                  {sentimentLabels[value]}
                </option>
              ))}
            </select>
          </label>
          {complaints && <label className="grid-filter">
            <span>Severity</span>
            <select
              aria-label="Severity"
              value={filters.severity ?? ""}
              onChange={(event) => update({ severity: event.target.value || undefined })}
            >
              <option value="">All intensities</option>
              {["critical", "high", "medium", "low"].map((value) => (
                <option key={value} value={value}>
                  {severityLabels[value]}
                </option>
              ))}
            </select>
          </label>}
          <label className="grid-filter">
            <span>Type {noun}</span>
            <select
              aria-label={`Type ${noun}`}
              value={filters.kind ?? ""}
              onChange={(event) => update({ kind: event.target.value || undefined })}
            >
              <option value="">All types</option>
              <option value="text">Text only</option>
              <option value="voice">Audio only</option>
              {!complaints && <option value="voc">Customer survey</option>}
            </select>
          </label>
          <label className="grid-filter">
            <span>Analysis status</span>
            <select
              aria-label="Analysis status"
              value={filters.analysis ?? ""}
              onChange={(event) => update({ analysis: event.target.value || undefined })}
            >
              <option value="">Any analysis situation</option>
              <option value="analyzed">Analyzed</option>
              <option value="pending">Pending analysis</option>
              <option value="failed">Analysis failed</option>
            </select>
          </label>
        </div>
        </div>
      </GridFilters>

      {notice && (
        <div className="notice" role="status">
          <Icon name="check" size={17} />
          {notice}
        </div>
      )}
      <ErrorBox error={error} />

      <GridPanel className={`  ${loading && data ? "is-refreshing" : ""}`}>
        {loading && !data && (
          <div className="empty loading" role="status">
            <span className="spinner" /> Loading…
          </div>
        )}
        {data && data.items.length === 0 && (
          <Empty
            text={
              activeFilters || filters.status
                ? `${noun} was not found with these filters.`
                : `No ${noun.toLowerCase()} records yet.`
            }
          />
        )}
        {data && data.items.length > 0 && (
          <div className="table-scroll">
            <DataTable paginate={false} className="data-grid">
              <thead>
                <tr>
                  {!customerId && <th>Customer</th>}
                  <th className="col-text">{complaints ? "The text of the complaint" : "Feedback"}</th>
                  <th>Channel</th>
                  <th className="col-ai">Intelligent analysis</th>
                  <th>{complaints ? "Severity" : "Rating"}</th>
                  {complaints && <th>Status</th>}
                  <th>Registration time</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((row) => (
                  <tr
                    key={row.id}
                    className={`${row.id === selectedId ? "selected" : ""} ${complaints && row.status === "new" ? "unread" : ""}`}
                    tabIndex={0}
                    aria-label={`${noun} ${row.customer_name || row.customer_id || ""}`}
                    onClick={() => select(row.id)}
                    onKeyDown={(event) => {
                      if (event.target !== event.currentTarget) return;
                      if (event.key === "Enter" || event.key === " ") {
                        event.preventDefault();
                        select(row.id);
                      }
                    }}
                  >
                    {!customerId && (
                      <td className="col-customer">
                        {row.customer_id ? (
                          <Link
                            to={`/customers/${encodeURIComponent(row.customer_id)}`}
                            onClick={(event) => event.stopPropagation()}
                          >
                            {row.customer_name || row.customer_id}
                          </Link>
                        ) : (
                          <span className="muted">Unknown</span>
                        )}
                        {row.customer_name && <small className="mono">{row.customer_id}</small>}
                      </td>
                    )}
                    <td className="col-text">
                      <div className="text-cell">
                        <span className={`kind-icon ${row.kind}`} title={row.kind === "voice" ? "Voice message" : "Text"}>
                          <Icon name={row.kind === "voice" ? "voice" : "message"} size={14} />
                        </span>
                        <p dir="auto">
                          {feedbackComment(row.comment) ||
                            (row.kind === "voice" ? (
                              <span className="muted">Awaiting transcription…</span>
                            ) : (
                              <span className="muted">{complaints ? "The text of the complaint has not been registered." : "Points only"}</span>
                            ))}
                        </p>
                      </div>
                    </td>
                    <td className="nowrap">{label(channelLabels, row.channel)}</td>
                    <td className="col-ai">
                      <AnalysisCell row={row} />
                    </td>
                    <td>
                      {complaints ? <Badge value={row.severity} map={severityLabels} /> : row.nps_score != null ? <NpsBadge score={row.nps_score} /> : (
                        row.rating != null || row.csat_score != null ? <Stars value={row.csat_score ?? row.rating} /> :
                          row.ces_score != null ? <span>{fmtNumber(row.ces_score)} From 7 · Effort</span> : <span className="muted">—</span>
                      )}
                    </td>
                    {complaints && <td>
                      <Badge value={row.status} />
                    </td>}
                    <td className="small muted nowrap">{fmtDateTime(row.submitted_at)}</td>
                  </tr>
                ))}
              </tbody>
            </DataTable>
          </div>
        )}
        {data && (
          <Pagination
            page={data.page}
            pageSize={data.page_size}
            total={data.total}
            onPage={(page) => update({ page })}
            onPageSize={(pageSize) => update({ pageSize })}
          />
        )}
      </GridPanel>

      {selectedId && (
        <Modal title={`${noun} details`} drawer onClose={() => select(null)}>
          <FeedbackDetail
            key={selectedId}
            id={selectedId}
            inbox={inbox}
            analysisError={selectedRow?.analysis_state === "failed" ? selectedRow.analysis_error : null}
            onChanged={(message) => {
              setNotice(message);
              setNonce((n) => n + 1);
            }}
          />
        </Modal>
      )}
    </div>
  );
}
