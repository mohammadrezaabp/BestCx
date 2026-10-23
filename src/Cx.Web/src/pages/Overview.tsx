import { useState } from "react";
import { api } from "../api";
import { Link } from "react-router-dom";
import { BarList, Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import Icon from "../components/Icon";
import { NpsReport } from "../components/Nps";
import { ExperienceScores } from "../components/ExperienceScores";
import { fmtDate, fmtNumber, fmtPercent, label, sentimentLabels } from "../format";
import { useTopics } from "../hooks/useTopics";

function Metric({
  title,
  value,
  hint,
  tone = "",
}: {
  title: string;
  value: string;
  hint: string;
  tone?: string;
}) {
  return (
    <div className={`card metric ${tone}`}>
      <div className="metric-top">
        <span>{title}</span>
      </div>
      <strong>{value}</strong>
      <div className="small muted">{hint}</div>
    </div>
  );
}
function TrendChart({
  points,
}: {
  points: { date: string; events: number; negative: number; calls: number }[];
}) {
  if (points.length < 2)
    return <Empty text="More data are needed to demonstrate trends" />;
  const width = 660,
    height = 230,
    pad = 32;
  const max = Math.max(
    ...points.flatMap((p) => [p.events, p.negative, p.calls]),
    1,
  );
  const line = (key: "events" | "negative" | "calls") =>
    points
      .map(
        (p, i) =>
          `${i ? "L" : "M"}${pad + (i / (points.length - 1)) * (width - pad * 2)},${height - pad - (p[key] / max) * (height - pad * 2)}`,
      )
      .join(" ");
  return (
    <>
      <svg
        className="trend-chart"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label="Daily trend of interactions, negative reviews and calls"
      >
        <defs>
          <linearGradient id="trend-fill" x1="0" y1="0" x2="0" y2="1">
            <stop style={{ stopColor: "var(--chart-1)", stopOpacity: 0.1 }} />
            <stop offset="1" style={{ stopColor: "var(--chart-1)", stopOpacity: 0 }} />
          </linearGradient>
        </defs>
        {[0, 0.25, 0.5, 0.75, 1].map((ratio) => (
          <g key={ratio}>
            <line
              x1={pad}
              y1={height - pad - ratio * (height - pad * 2)}
              x2={width - pad}
              y2={height - pad - ratio * (height - pad * 2)}
              stroke="var(--border)"
              strokeDasharray="2 6"
            />
            <text
              x={width - 4}
              y={height - pad - ratio * (height - pad * 2) + 4}
              textAnchor="end"
              fill="var(--text-faint)"
              fontSize="10"
            >
              {fmtNumber(Math.round(max * ratio))}
            </text>
          </g>
        ))}
        <path
          d={`${line("events")} L${width - pad},${height - pad} L${pad},${height - pad} Z`}
          fill="url(#trend-fill)"
        />
        <path
          d={line("events")}
          stroke="var(--chart-1)"
          strokeWidth="2"
          fill="none"
        />
        <path
          d={line("negative")}
          stroke="var(--chart-2)"
          strokeWidth="1.75"
          fill="none"
        />
        <path
          d={line("calls")}
          stroke="var(--chart-3)"
          strokeWidth="1.75"
          fill="none"
          strokeDasharray="5 4"
        />
        {points.map((p, i) => (
          <circle
            key={p.date}
            cx={pad + (i / (points.length - 1)) * (width - pad * 2)}
            cy={height - pad - (p.events / max) * (height - pad * 2)}
            r="3"
            fill="var(--surface)"
            stroke="var(--chart-1)"
            strokeWidth="1.75"
          >
            <title>
              {fmtDate(p.date)}: {fmtNumber(p.events)} interaction,{" "}
              {fmtNumber(p.negative)} negative or mixed, {fmtNumber(p.calls)} Call
            </title>
          </circle>
        ))}
      </svg>
      <div className="chart-dates" dir="ltr">
        <span>{fmtDate(points[0].date)}</span>
        <span>{fmtDate(points[points.length - 1].date)}</span>
      </div>
    </>
  );
}
export default function Overview() {
  const topics = useTopics();
  const [days, setDays] = useState(7);
  const { data, error, loading, reload } = useAsync(
    () => api.overview(days),
    [days],
  );
  const experience = useAsync(() => {
    const to = new Date();
    return api.reports({ inbox: "feedback", from: new Date(to.getTime() - days * 86400000).toISOString(), to: to.toISOString(), timeZone: "UTC" });
  }, [days]);
  const totalSentiment =
    data?.sentiment_breakdown.reduce((sum, item) => sum + item.count, 0) ?? 0;
  return (
    <div className="overview-dashboard">
      <PageHead
        title="Overview"
        actions={
          <>
            <select
              aria-label="Time frame"
              value={days}
              onChange={(event) => setDays(Number(event.target.value))}
            >
              <option value={1}>Last 24 hours</option>
              <option value={7}>Last 7 days</option>
              <option value={30}>Last 30 days</option>
              <option value={90}>Last 90 days</option>
            </select>
            <button
              className="icon-button"
              aria-label="Dashboard update"
              onClick={() => { reload(); experience.reload(); }}
              disabled={loading}
            >
              <Icon name="refresh" size={18} />
            </button>
          </>
        }
      />
      <ErrorBox error={error} />
      {Boolean(error) && <button onClick={reload}>Retry</button>}
      {loading && <Loading />}
      {data && (
        <>
          <div className="grid metric-grid">
            <Metric
              title="Customer interactions"
              value={fmtNumber(data.total_interactions)}
              hint={`${fmtNumber(data.active_customers)} Active client in this period`}
            />
            <Metric
              title="Received feedback"
              value={fmtNumber(data.feedback_count)}
              hint={`${fmtNumber(data.support_cases)} Support ticket`}
            />
            <Metric
              title="Voice calls"
              value={fmtNumber(data.call_count)}
              hint="Calls recorded in this interval"
            />
            <Metric
              title="Negative or mixed feeling"
              value={
                totalSentiment ? fmtPercent(data.negative_sentiment_share) : "—"
              }
              hint={
                totalSentiment
                  ? "From the analyzes performed in this period"
                  : "No analysis has been recorded yet"
              }
              tone={data.negative_sentiment_share > 0.3 ? "attention" : ""}
            />
          </div>
          <ErrorBox error={experience.error} />
          <div className="overview-experience">
            <NpsReport summary={data.nps} compact />
            {experience.data && <ExperienceScores report={experience.data} showDescriptions={false} compact />}
          </div>
          <div className="overview-insights">
            <section className="card trend-card">
              <div className="section-head">
                <div>
                  <h2>Voice of the Customer trends</h2>
                </div>
                <span className="badge">{fmtNumber(days)} Last day</span>
              </div>
              <div className="chart-legend">
                <span>
                  <i />
                  Interactions
                </span>
                <span>
                  <i className="negative" />
                  Negative or mixed
                </span>
                <span>
                  <i className="calls" />
                  Contacts
                </span>
              </div>
              <TrendChart points={data.daily_trend} />
            </section>
            <section className="card">
              <div className="section-head">
                <div>
                  <h2>Feeling of customers</h2>
                </div>
              </div>
              {totalSentiment ? (
                <>
                  <div className="sentiment-stack" aria-hidden="true">
                    {data.sentiment_breakdown.map((item) => (
                      <span
                        key={item.name}
                        className={item.name}
                        style={{ flex: item.count }}
                      />
                    ))}
                  </div>
                  <div className="sentiment-list">
                    {data.sentiment_breakdown.map((item) => (
                      <div key={item.name}>
                        <span>
                          <i className={`sentiment-dot ${item.name}`} />
                          {label(sentimentLabels, item.name)}
                        </span>
                        <strong>
                          {fmtPercent(item.count / totalSentiment)}{" "}
                          <small>{fmtNumber(item.count)} Analysis</small>
                        </strong>
                      </div>
                    ))}
                  </div>
                </>
              ) : (
                <Empty text="No analysis has been recorded yet" />
              )}
            </section>
            <section className="card">
              <div className="section-head">
                <div>
                  <h2>Frequent topics</h2>
                </div>
                <Link className="small" to="/experience/reports">Full report</Link>
              </div>
              <BarList
                items={data.top_pain_points.slice(0, 3).map((item) => ({
                  ...item,
                  name: topics.label(item.name),
                }))}
              />
            </section>
          </div>
          {data.total_interactions === 0 && (
            <p className="overview-empty-note">No interactions have been recorded yet.</p>
          )}
          <div className="page-footnote">
            Reporting interval: {fmtDate(data.from)} to {fmtDate(data.to)}
          </div>
        </>
      )}
    </div>
  );
}
