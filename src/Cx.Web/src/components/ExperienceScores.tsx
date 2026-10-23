import type { ExperienceReport } from "../api";
import { BarList, Empty } from "./Common";
import { fmtNumber } from "../format";

function CompactDistribution({ items }: { items: { name: string; count: number }[] }) {
  const max = Math.max(1, ...items.map(item => item.count));
  return <div className="score-distribution" dir="ltr">
    {items.map(item => <div key={item.name} title={`Rating ${fmtNumber(Number(item.name))}: ${fmtNumber(item.count)} Answer`}>
      <div className="score-column"><i style={{ height: `${item.count / max * 100}%` }} /></div>
      <span>{fmtNumber(Number(item.name))}</span>
    </div>)}
  </div>;
}

export function ExperienceScores({ report, showDescriptions = true, compact = false }: {
  report: ExperienceReport; showDescriptions?: boolean; compact?: boolean;
}) {
  const s = report.summary;
  return (
    <div className="grid experience-grid">
      <section className="card" aria-label="Customer satisfaction report">
        <div className="section-head">
          <h2>{compact ? "Satisfaction · CSAT" : "Customer satisfaction · CSAT"}</h2>
          <strong className="experience-score">
            {s.csat_percent == null ? "—" : `${fmtNumber(s.csat_percent)}%`}
          </strong>
        </div>
        <p className="muted small">
          {fmtNumber(s.csat_responses)} Answer
          {showDescriptions && " · Percentage of points 4 and 5 from valid answers 1 to 5"}
        </p>
        {s.csat_responses ? compact ? <CompactDistribution items={report.csat_distribution} /> : (
          <BarList
            items={report.csat_distribution.map((x) => ({
              ...x,
              name: ["Very unhappy", "Dissatisfied", "Medium", "Satisfied", "Very satisfied"][
                Number(x.name) - 1
              ],
            }))}
          />
        ) : (
          <Empty text="No response has yet been registered for consent" />
        )}
      </section>
      <section className="card" aria-label="Ease of doing work report">
        <div className="section-head">
          <h2>{compact ? "Effort · CES" : "Ease of use · CES"}</h2>
          <strong className="experience-score">
            {s.ces_average == null ? "—" : `${fmtNumber(s.ces_average)} / 7`}
          </strong>
        </div>
        <p className="muted small">
          {fmtNumber(s.ces_responses)} Answer
          {showDescriptions && " · 1: Very difficult, 7: Very easy · Average valid responses"}
        </p>
        {s.ces_responses ? compact ? <CompactDistribution items={report.ces_distribution} /> : (
          <>
            <BarList
              items={report.ces_distribution.map((x) => ({
                ...x,
                name: `Rating ${fmtNumber(Number(x.name))}`,
              }))}
            />
            {showDescriptions && <p className="small muted">
              {fmtNumber(s.ces_easy_percent)}% of the answers have a score of 5 to 7.
            </p>}
          </>
        ) : (
          <Empty text="No response has yet been filed for convenience" />
        )}
      </section>
    </div>
  );
}
