import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { api, type ExperienceReport, type ReportGroup } from "../api";
import { Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { ExperienceScores } from "../components/ExperienceScores";
import TopicSelects, { rootTopicCode } from "../components/TopicSelects";
import CustomerSelect from "../components/CustomerSelect";
import DatePicker from "../components/DatePicker";
import { fmtDate, fmtNumber, label, sentimentLabels, severityLabels, statusLabels, channelLabels, channelOptions } from "../format";
import { useTopics } from "../hooks/useTopics";
import type { InboxKind } from "../feedback";
import { caseStatuses } from "../components/OperationsCommon";

const timeZone = "UTC";
function today() {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(new Date());
}
function shiftDay(date: string, days: number) {
  const d = new Date(`${date}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
const split = (v: string | null) => v?.split(",").filter(Boolean) ?? [];
const readDraft = (params: URLSearchParams) => ({
  from: params.get("start") ?? shiftDay(today(), -29),
  to: params.get("end") ?? today(),
  topics: split(params.get("topics")),
  sentiments: split(params.get("sentiments")),
  channels: split(params.get("channels")),
  severities: split(params.get("severities")),
  types: split(params.get("types")),
  statuses: split(params.get("statuses")),
  q: params.get("q") ?? "",
  bucket: params.get("bucket") ?? "day",
  customerId: params.get("customerId") ?? "",
});

function ReportSelect({
  title,
  placeholder,
  value,
  options,
  onChange,
}: {
  title: string;
  placeholder: string;
  value: string[];
  options: { value: string; label: string }[];
  onChange: (v: string[]) => void;
}) {
  const selected = value.join(",");
  const selection = value.map((v) => options.find((o) => o.value === v)?.label ?? v).join(", ");
  return (
    <label className="grid-filter">
      <span>{title}</span>
      <select aria-label={title} value={selected}
        onChange={event => onChange(event.target.value ? event.target.value.split(",") : [])}>
        <option value="">{placeholder}</option>
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
        {selected && !options.some(o => o.value === selected) && <option value={selected}>{selection}</option>}
      </select>
    </label>
  );
}

function ReportTrend({ rows }: { rows: ReportGroup[] }) {
  const width = 780,
    height = 210,
    pad = 28;
  const x = (i: number) =>
    pad + (i / Math.max(rows.length - 1, 1)) * (width - pad * 2);
  const y = (v: number) => height - pad - (v / 100) * (height - pad * 2);
  const metrics: {
    key: "csat_percent" | "ces_average";
    color: string;
    factor: number;
    title: string;
  }[] = [
    { key: "csat_percent", color: "var(--chart-1)", factor: 1, title: "CSAT" },
  ];
  if (!rows.some((r) => r.csat_responses))
    return <Empty text="No answer was registered to show the process of points" />;
  return (
    <>
      <svg
        className="trend-chart"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label="The trend of customer satisfaction percentage"
      >
        {[0, 25, 50, 75, 100].map((v) => (
          <g key={v}>
            <line
              x1={pad}
              x2={width - pad}
              y1={y(v)}
              y2={y(v)}
              stroke="var(--border)"
              strokeDasharray="3 5"
            />
            <text
              x={width - 2}
              y={y(v) + 4}
              fontSize="10"
              fill="var(--text-faint)"
              textAnchor="end"
            >
              {fmtNumber(v)}%
            </text>
          </g>
        ))}
        {metrics.map((m) => {
          let previous = false;
          const path = rows
            .map((r, i) => {
              const v = r[m.key];
              if (v == null) {
                previous = false;
                return "";
              }
              const segment = `${previous ? "L" : "M"}${x(i)},${y(v * m.factor)}`;
              previous = true;
              return segment;
            })
            .join(" ");
          return (
            <g key={m.key}>
              <path d={path} fill="none" stroke={m.color} strokeWidth="2" />
              {rows.map((r, i) =>
                r[m.key] == null ? null : (
                  <circle
                    key={r.name}
                    cx={x(i)}
                    cy={y(r[m.key]! * m.factor)}
                    r="4"
                    fill={m.color}
                  >
                    <title>
                      {fmtDate(`${r.name}T12:00:00+00:00`)}: {m.title}{" "}
                      {fmtNumber(r[m.key])},{" "}
                      {fmtNumber(
                        m.key === "csat_percent"
                          ? r.csat_responses
                          : r.ces_responses,
                      )}{" "}
                      Answer
                    </title>
                  </circle>
                ),
              )}
            </g>
          );
        })}
      </svg>
      <div className="chart-dates" dir="ltr">
        <span>{fmtDate(rows[0]?.name)}</span>
        <span>{fmtDate(rows.at(-1)?.name)}</span>
      </div>
    </>
  );
}

function VolumeTrend({ rows }: { rows: ReportGroup[] }) {
  const max = Math.max(1, ...rows.map((r) => r.total));
  const width = 780,
    height = 180,
    pad = 20,
    slot = (width - 2 * pad) / Math.max(rows.length, 1);
  if (!rows.some((r) => r.total))
    return <Empty text="To show the trend, no feedback is recorded" />;
  return (
    <>
      <svg
        className="trend-chart"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label="The trend of the number of registered cases"
      >
        {rows.map((r, i) => (
          <g key={r.name}>
            <rect
              x={pad + i * slot}
              y={height - pad - (r.total / max) * (height - pad * 2)}
              width={Math.max(1, slot * 0.7)}
              height={(r.total / max) * (height - pad * 2)}
              fill="var(--chart-1)"
            >
              <title>
                {fmtDate(`${r.name}T12:00:00+00:00`)}: {fmtNumber(r.total)}{" "}
                Registered case
              </title>
            </rect>
          </g>
        ))}
      </svg>
      <div className="chart-dates" dir="ltr">
        <span>{fmtDate(rows[0]?.name)}</span>
        <span>{fmtDate(rows.at(-1)?.name)}</span>
      </div>
    </>
  );
}

// Neutralize spreadsheet formulas and retain English UTF-8 in the downloaded CSV.
function cell(value: unknown) {
  let text = value == null ? "" : String(value);
  if (typeof value === "string" && /^[\s]*[=+\-@]/.test(text))
    text = `'${text}`;
  return `"${text.replaceAll('"', '""')}"`;
}
function exportReport(report: ExperienceReport, filters: URLSearchParams, topicLabel: (v: string) => string, inbox: InboxKind) {
  const complaints = inbox === "complaints";
  const rows: unknown[][] = [
    [complaints ? "Complaints report" : "Customer Experience Report"], ["of", report.from, "to (Exclusive)", report.to],
    ["Time zone", timeZone], ["Filters", filters.toString()],
    complaints ? ["Section", "Title", "Complaints", "Customers", "Open", "Resolved", "Outside the deadline"] :
      ["Section", "Title", "Feedback", "Customers", "Satisfaction response", "Percentage satisfaction", "The answer is ease", "Ease of 7", "Recommendation response", "Recommendation"],
  ];
  const add = (section: string, name: string, m: ExperienceReport["summary"]) => rows.push(complaints ?
    [section, name, m.total, m.customers, m.open, m.resolved, m.overdue] :
    [section, name, m.total, m.customers, m.csat_responses, m.csat_percent, m.ces_responses, m.ces_average, m.nps_responses, m.nps_score]);
  add("Total", "All", report.summary);
  const groups: { key: "trend" | "topics" | "channels" | "severities" | "statuses" | "types" | "sentiments"; title: string; name: (value: string) => string }[] = [
    { key: "trend", title: "Trend", name: value => value }, { key: "topics", title: "Topic", name: topicLabel },
    { key: "channels", title: "Channel", name: value => label(channelLabels, value) },
    ...(complaints ? [
      { key: "severities" as const, title: "Severity", name: (value: string) => label(severityLabels, value) },
      { key: "statuses" as const, title: "Status", name: (value: string) => label(caseStatuses, value) },
    ] : [
      { key: "types" as const, title: "Registration type", name: (value: string) => label(statusLabels, value) },
      { key: "sentiments" as const, title: "Feeling", name: (value: string) => label(sentimentLabels, value) },
    ]),
  ];
  for (const group of groups) for (const row of report[group.key]) add(group.title, group.name(row.name), row);
  if (!complaints) {
    rows.push([], ["Index", "Rating", "Number of responses"]);
    for (const [key, title] of [["csat_distribution", "Satisfaction"], ["ces_distribution", "Effort"]] as const)
      for (const row of report[key]) rows.push([title, row.name, row.count]);
  }
  const url = URL.createObjectURL(new Blob(["\uFEFF", rows.map(row => row.map(cell).join(",")).join("\r\n")], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a"); link.href = url; link.download = `cx-${inbox}-report-${today()}.csv`; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export default function Reports({ inbox }: { inbox: InboxKind }) {
  const complaints = inbox === "complaints";
  const topics = useTopics();
  const [params, setParams] = useSearchParams();
  const [draft, setDraft] = useState(() => readDraft(params));
  const [view, setView] = useState("overview");
  const [breakdown, setBreakdown] = useState("topics");
  const [dateError, setDateError] = useState("");
  const [dateResetKey, setDateResetKey] = useState(0);
  const applied = params.toString();
  useEffect(() => { setDraft(readDraft(new URLSearchParams(applied))); }, [applied, inbox]);
  const { data, loading, error, reload } = useAsync(() => {
    const start = params.get("start") ?? shiftDay(today(), -29), end = params.get("end") ?? today();
    if (!/^\d{4}-\d{2}-\d{2}$/.test(start) || !/^\d{4}-\d{2}-\d{2}$/.test(end) ||
      Number.isNaN(Date.parse(start)) || Number.isNaN(Date.parse(end)) || start > end ||
      (Date.parse(end) - Date.parse(start)) / 86400000 >= 366) throw new Error("The time period must be valid and maximum 366 days.");
    return api.reports({ inbox,
      from: new Date(`${start}T00:00:00+00:00`).toISOString(), to: new Date(`${shiftDay(end, 1)}T00:00:00+00:00`).toISOString(), timeZone,
      topics: params.get("topics") ?? undefined, sentiments: complaints ? undefined : params.get("sentiments") ?? undefined,
      channels: params.get("channels") ?? undefined, severities: complaints ? params.get("severities") ?? undefined : undefined,
      types: complaints ? undefined : params.get("types")?.split(",").filter(value => value !== "complaint").join(",") || undefined, q: params.get("q") ?? undefined,
      statuses: complaints ? params.get("statuses") ?? undefined : undefined, bucket: params.get("bucket") ?? "day", customerId: params.get("customerId") ?? undefined,
    });
  }, [applied, inbox]);
  const topicName = (code: string) => topics.byCode.get(code)?.path ?? topics.label(code);
  const rootCode = draft.topics.length === 1 ? rootTopicCode(topics.options, draft.topics[0]) : undefined;
  const parentTopic = rootCode ?? draft.topics.join(",");
  const childTopic = rootCode && rootCode !== draft.topics[0] ? draft.topics[0] : "";
  const extraParents = [...new Set([...(data?.available_topics ?? []), ...draft.topics, parentTopic])]
    .filter(code => code && !topics.byCode.has(code)).map(code => ({ value: code, label: code.split(",").map(topicName).join(", ") }));
  const options = (map: Record<string, string>) => Object.entries(map).map(([value, text]) => ({ value, label: text }));
  const advancedCount = [draft.q, draft.customerId, draft.topics.length, draft.sentiments.length, draft.channels.length, draft.severities.length].filter(Boolean).length;
  function apply() {
    if (!draft.from || !draft.to || Number.isNaN(Date.parse(draft.from)) || Number.isNaN(Date.parse(draft.to)) || draft.from > draft.to ||
      (Date.parse(draft.to) - Date.parse(draft.from)) / 86400000 >= 366) {
      setDateError("The time period must be valid and maximum 366 days."); return;
    }
    setDateError("");
    const next = new URLSearchParams({ start: draft.from, end: draft.to, bucket: draft.bucket });
    for (const key of ["topics", "channels", "severities", "statuses", "sentiments", "types"] as const) {
      if (complaints ? ["sentiments", "types"].includes(key) : ["severities", "statuses"].includes(key)) continue;
      if (draft[key].length) next.set(key, draft[key].join(","));
    }
    if (draft.q.trim()) next.set("q", draft.q.trim());
    if (draft.customerId) next.set("customerId", draft.customerId);
    if (next.toString() === applied) reload(); else setParams(next);
  }
  const s = data?.summary;
  const groups = data ? {
    topics: { title: "Topic", rows: data.topics, name: topicName },
    severities: { title: "Severity", rows: data.severities, name: (v: string) => label(severityLabels, v) },
    statuses: { title: "Status of proceedings", rows: data.statuses, name: (v: string) => label(caseStatuses, v) },
    channels: { title: "Channel", rows: data.channels, name: (v: string) => label(channelLabels, v) },
    types: { title: "Registration type", rows: data.types, name: (v: string) => label(statusLabels, v) },
    sentiments: { title: "Sentiment", rows: data.sentiments, name: (v: string) => label(sentimentLabels, v) },
  } : null;
  const group = groups?.[breakdown as keyof NonNullable<typeof groups>];
  return <>
    <PageHead title={complaints ? "Complaint reports" : "Experience reports"} actions={<>
      <button disabled={!data || loading} onClick={() => data && exportReport(data, params, topicName, inbox)}>Receive CSV</button>
      <button disabled={loading} onClick={reload}>Update</button>
    </>} />
    <GridFilters as="form" className=" report-filters" onSubmit={event => { event.preventDefault(); apply(); }}>
      <div className="report-primary-filters">
        <DatePicker key={`start-${dateResetKey}`} label="Start date" required value={draft.from} onChange={value => setDraft({ ...draft, from: value })} />
        <DatePicker key={`end-${dateResetKey}`} label="End date" required value={draft.to} onChange={value => setDraft({ ...draft, to: value })} />
        <ReportSelect title={complaints ? "Status of proceedings" : "Registration type"} placeholder="All items" value={complaints ? draft.statuses : draft.types.filter(value => value !== "complaint")} options={options(complaints ? caseStatuses : { generic: "Comment", voc: "Questionnaire", voice: "Audio", csat: "Satisfaction", nps: "Recommendation", rating: "Rating" })} onChange={value => setDraft(complaints ? { ...draft, statuses: value } : { ...draft, types: value })} />
        <div className="toolbar report-filter-actions"><button className="primary" type="submit" disabled={loading}>Display report</button>
          <button type="button" onClick={() => { setDraft(readDraft(new URLSearchParams())); setParams({}); setDateError(""); setDateResetKey(key => key + 1); }}>Clear filters</button>
        </div>
      </div>
      <details className="report-more-filters"><summary>More filters {advancedCount > 0 && <span className="count">{fmtNumber(advancedCount)}</span>}</summary>
        <div className="report-filter-grid">
          <label className="grid-filter"><span>Search in text</span><input maxLength={200} value={draft.q} onChange={event => setDraft({ ...draft, q: event.target.value })} placeholder="The desired expression…" /></label>
          <CustomerSelect value={draft.customerId} onChange={customerId => setDraft({ ...draft, customerId })} />
          <TopicSelects options={topics.options} parentValue={parentTopic} childValue={childTopic} extraParents={extraParents}
            onParentChange={value => setDraft({ ...draft, topics: value ? value.split(",") : [] })}
            onChildChange={value => setDraft({ ...draft, topics: value ? [value] : parentTopic ? [parentTopic] : [] })} />
          <ReportSelect title="Channel" placeholder="All channels" value={draft.channels} options={[...new Set([...channelOptions, ...(data?.available_channels ?? []), ...draft.channels])].map(value => ({ value, label: label(channelLabels, value) }))} onChange={channels => setDraft({ ...draft, channels })} />
          {!complaints && <ReportSelect title="Sentiment" placeholder="All emotions" value={draft.sentiments} options={options(sentimentLabels)} onChange={sentiments => setDraft({ ...draft, sentiments })} />}
          {complaints && <ReportSelect title="Severity" placeholder="All intensities" value={draft.severities} options={options({ ...severityLabels, unknown: "Unknown" })} onChange={severities => setDraft({ ...draft, severities })} />}
          <label className="grid-filter"><span>Time grouping</span><select value={draft.bucket} onChange={event => setDraft({ ...draft, bucket: event.target.value })}><option value="day">Daily</option><option value="week">Weekly</option><option value="month">Monthly</option></select></label>
        </div>
      </details>
      {dateError && <div className="error" role="alert">{dateError}</div>}
    </GridFilters>
    <ErrorBox error={error} /><ErrorBox error={topics.error} />
    {Boolean(error) && <button onClick={reload}>Retry</button>}
    {loading && <Loading />}
    {data && s && (s.total === 0 ? <Empty text="In this period and with these filters, no case has been registered" /> : <>
      <div className="grid report-metrics">
        {(complaints ? [["Registered complaints", fmtNumber(s.total)], ["Pending proceedings", fmtNumber(s.open)], ["Resolved", fmtNumber(s.resolved)], ["Outside the deadline", fmtNumber(s.overdue)]] : [["Feedback", fmtNumber(s.total)], ["Customers", fmtNumber(s.customers)], ["Customer satisfaction", s.csat_percent == null ? "—" : `${fmtNumber(s.csat_percent)}%`], ["Advice to others", fmtNumber(s.nps_score)]]).map(([title, value]) =>
          <section className="card metric" key={title}><span>{title}</span><strong>{value}</strong></section>)}
      </div>
      <div className="tabs report-tabs" role="group" aria-label="Report view">
        {[["overview", "Submission trend"], ...(!complaints ? [["scores", "Customer scores"]] : []), ["breakdown", "Breakdown"]].map(([key, title]) =>
          <button key={key} className={view === key ? "active" : ""} aria-pressed={view === key} onClick={() => setView(key)}>{title}</button>)}
      </div>
      {view === "overview" && <section className="card"><h2>Submission trend</h2><VolumeTrend rows={data.trend} /></section>}
      {!complaints && view === "scores" && <><ExperienceScores report={data} showDescriptions={false} compact />
        <section className="card"><h2>Process of customer satisfaction</h2><ReportTrend rows={data.trend} /></section></>}
      {view === "breakdown" && group && <GridPanel>
        <div className="grid-toolbar"><label className="grid-filter"><span>Separation based on</span><select value={breakdown} onChange={event => setBreakdown(event.target.value)}><option value="topics">Topic</option><option value="channels">Channel</option>{complaints ? <><option value="severities">Severity</option><option value="statuses">Status of proceedings</option></> : <><option value="types">Registration type</option><option value="sentiments">Sentiment</option></>}</select></label></div>
        {!group.rows.length ? <Empty /> : <DataTable key={breakdown} aria-label="Breakdown of the report"><thead><tr><th>{group.title}</th><th>Count</th>{complaints ? <><th>Open</th><th>Resolved</th><th>Outside the deadline</th></> : <><th>Customer satisfaction</th><th>Ease of 7</th></>}</tr></thead><tbody>
          {group.rows.map(row => <tr key={row.name}><td>{group.name(row.name)}</td><td>{fmtNumber(row.total)}</td>{complaints ? <><td>{fmtNumber(row.open)}</td><td>{fmtNumber(row.resolved)}</td><td>{fmtNumber(row.overdue)}</td></> : <><td>{row.csat_percent == null ? "—" : `${fmtNumber(row.csat_percent)}%`}<small>{fmtNumber(row.csat_responses)} Answer</small></td><td>{fmtNumber(row.ces_average)}<small>{fmtNumber(row.ces_responses)} Answer</small></td></>}</tr>)}
        </tbody></DataTable>}
      </GridPanel>}
    </>)}
  </>;
}
