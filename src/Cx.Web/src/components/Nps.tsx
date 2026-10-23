import type { NpsSummary } from "../api";
import { fmtNumber } from "../format";

export function npsGroup(score: number) {
  return score >= 9 ? "Promoter" : score >= 7 ? "Passive" : "Detractor";
}

export function NpsBadge({ score }: { score: number }) {
  if (!Number.isInteger(score) || score < 0 || score > 10) return <span>—</span>;
  return (
    <span className={`badge ${score >= 9 ? "positive" : score >= 7 ? "neutral" : "negative"}`}>
      NPS: {fmtNumber(score)} From 10 · {npsGroup(score)}
    </span>
  );
}

export function NpsInput({ value, onChange }: {
  value: number | null;
  onChange: (value: number) => void;
}) {
  return (
    <fieldset className="plain-fieldset nps-input">
      <legend>How likely are you to recommend our product or company to a friend or colleague?</legend>
      <div className="nps-options" dir="ltr">
        {Array.from({ length: 11 }, (_, score) => (
          <label key={score}>
            <input type="radio" name="nps" value={score} checked={value === score}
              onChange={() => onChange(score)} aria-label={`NPS ${score}`} required />
            <span>{fmtNumber(score)}</span>
          </label>
        ))}
      </div>
      <p className="small muted">0: Not at all likely · 10: Very likely</p>
      {value !== null && <NpsBadge score={value} />}
    </fieldset>
  );
}

export function NpsReport({ summary, compact = false }: { summary?: NpsSummary; compact?: boolean }) {
  const score = summary?.score;
  return (
    <section className="card nps-report" aria-label="Report NPS">
      <div className="section-head">
        <h2>NPS score</h2>
        <strong className="nps-score" dir="ltr">
          {score == null ? "—" : `${score > 0 ? "+" : ""}${fmtNumber(score)}`}
        </strong>
      </div>
      {summary && summary.total > 0 ? (
        <>
          <p>{fmtNumber(summary.total)} responses{!compact && " In the selected interval"}</p>
          <div className="nps-breakdown">
            {([
              ["Promoters (9–10)", summary.promoters, summary.promoter_percent],
              ["Passives (7–8)", summary.passives, summary.passive_percent],
              ["Detractors (0–6)", summary.detractors, summary.detractor_percent],
            ] as const).map(([title, count, percent]) => (
              <div key={title}>
                <strong>{compact ? title.replace(/ \(.*\)$/, "") : title}</strong>
                <div title={`${fmtNumber(count)} responses · ${fmtNumber(percent ?? 0)}%`}>
                  {fmtNumber(count)}{!compact && " responses"} · {fmtNumber(percent ?? 0)}%
                </div>
              </div>
            ))}
          </div>
        </>
      ) : null}
    </section>
  );
}
