import { useEffect, useState } from "react";
import { operations, type Journey, type JourneyExperience, type JourneyStage } from "../operationsApi";
import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { dateRange, OpsField, useOperation } from "../components/OperationsCommon";
import DatePicker from "../components/DatePicker";
import Modal from "../components/Modal";
import Icon from "../components/Icon";
import { todayIso } from "../calendarDate";
import { fmtNumber } from "../format";

const emotions: Record<string, string> = { unknown: "Not yet reviewed", negative: "Dissatisfied / Concerned", neutral: "Neutral", mixed: "Mixed feeling", positive: "Satisfied / Sure" };
const emptyExperience: JourneyExperience = {
  goal: "", actions: "", touchpoints: "", thoughts: "", emotion: "unknown", feeling: "", pain_points: "",
  opportunities: "", evidence: "", evidence_status: "hypothesis", success_metric: "", owner: "",
};
const experienceFields = [
  ["goal", "The purpose and needs of the customer", "What does the customer want to achieve at this stage?"],
  ["actions", "Customer Actions", "What does it do?"],
  ["touchpoints", "Contact points and channels", "For example, website, contact with an expert, email or branch"],
  ["thoughts", "Customer thoughts and questions", "What question or doubt is in his mind?"],
  ["feeling", "Description of emotion and motivation", "What does it feel like and why?"],
  ["pain_points", "Obstacles and pain points", "What causes difficulty, worry or waste of time?"],
  ["opportunities", "Improvement opportunity and proposed action", "What change would make this experience better?"],
  ["success_metric", "Success criteria", "For example, reducing the waiting time or improving the satisfaction of this stage"],
  ["owner", "Responsible for improvement", "The name of the team or person pursuing this opportunity"],
] as const;
const mapRows = [
  ["goal", "The purpose and needs of the customer"], ["actions", "Customer Actions"], ["touchpoints", "Touchpoints"],
  ["thoughts", "Thoughts and questions"], ["feeling", "Sentiment"], ["pain_points", "Obstacles and pain points"],
  ["opportunities", "Improvement opportunities"], ["success_metric", "Success criteria"], ["owner", "Responsible for improvement"], ["evidence", "Evidence and sources"],
] as const;

function blankStage(title = ""): JourneyStage { return { title, event_names: [], experience: { ...emptyExperience } }; }
function normalize(journey: Journey): Journey {
  return { ...journey, persona: journey.persona ?? "", scenario: journey.scenario ?? "", goal: journey.goal ?? "", map_type: journey.map_type ?? "current_state",
    stages: journey.stages.map(stage => ({ ...stage, event_names: [...stage.event_names], experience: { ...emptyExperience, ...stage.experience } })) };
}
function createJourney(template: boolean): Journey {
  return { id: crypto.randomUUID(), name: "", persona: "", scenario: "", goal: "", map_type: "current_state", enabled: true, window_days: 14,
    stages: (template ? ["Acquaintance", "Review and comparison", "Buy and start using", "Use and continuity of communication", "Loyalty and recommendation"] : ["", ""]).map(blankStage) };
}

export default function JourneyMap({ manager }: { manager: boolean }) {
  const definitions = useAsync(operations.journeys, [], { keepDataOnReload: true });
  const [selected, setSelected] = useState("");
  const [editing, setEditing] = useState<Journey | null>(null);
  const [notice, setNotice] = useState("");
  useEffect(() => {
    if (!selected && definitions.data?.length) setSelected(definitions.data[0].id);
  }, [definitions.data, selected]);
  const journey = definitions.data?.find(item => item.id === selected);
  const stages = journey?.stages ?? [];
  const researched = stages.filter(stage => stage.experience?.evidence_status === "researched" && stage.experience.evidence?.trim()).length;

  return <>
    <PageHead title="Customer journey map" subtitle="The customer’s experience from his own point of view; From the first contact to the continuation of the relationship"
      actions={manager && <button className="primary" onClick={() => setEditing(createJourney(true))}><Icon name="plus" size={16} /> New map</button>} />
    {notice && <div className="notice" role="status">{notice}</div>}
    <ErrorBox error={definitions.error} />
    {definitions.error && <button onClick={definitions.reload}>Retry</button>}
    {definitions.loading && !definitions.data ? <Loading /> : !definitions.data?.length ? (
      <section className="card journey-empty">
        <Icon name="journey" size={40} /><h2>What experience does the customer have in each step?</h2>
        <p>Select a customer group and a scenario; Then map out the goals, touchpoints, emotions, and obstacles for each step.</p>
        <div className="journey-empty-steps">{["Customer and target", "Steps and contact points", "Feelings and obstacles", "Evidence and improvement"].map((title, index) => <span key={title}><b>{fmtNumber(index + 1)}</b>{title}</span>)}</div>
        {manager ? <div className="toolbar"><button className="primary" onClick={() => setEditing(createJourney(true))}>Starting with an editable template</button>
          <button onClick={() => setEditing(createJourney(false))}>Blank map</button></div> : <p className="muted small">No map has been registered yet.</p>}
        <p className="small muted">The template only suggests the title of the steps; The content of the experience should come from your own research and data.</p>
      </section>
    ) : <>
      <GridFilters className="journey-picker">
        <OpsField label="Map selection"><select value={selected} onChange={event => { setSelected(event.target.value); setNotice(""); }}>
          {definitions.data.map(item => <option key={item.id} value={item.id}>{item.name}{item.enabled ? "" : " (Archived)"}</option>)}
        </select></OpsField>
        {manager && journey && <button onClick={() => setEditing(normalize(journey))}><Icon name="edit" size={15} /> Map editing</button>}
      </GridFilters>
      {journey && <>
        <section className="card journey-context">
          <div className="section-head"><h2>{journey.name}</h2><span className="badge">{journey.map_type === "future_state" ? "Desired experience" : "Current experience"}</span></div>
          <dl><div><dt>Customer / Persona</dt><dd>{journey.persona || "The customer group has not yet been determined."}</dd></div>
            <div><dt>The scenario and scope of travel</dt><dd>{journey.scenario || "Specify the start, end and position of the client."}</dd></div>
            <div><dt>The purpose of this map</dt><dd>{journey.goal || "The goal of research or improvement has not yet been determined."}</dd></div></dl>
          <div className="journey-map-meta"><span>{fmtNumber(stages.length)} Stage</span><span>{fmtNumber(researched)} Stage with recorded evidence</span>
            {researched < stages.length && <span>Stages without evidence are hypotheses and require investigation.</span>}</div>
        </section>
        <GridPanel className="journey-map-panel" title="Experience map during the trip" actions={<span className="small muted">To see the continuation of the steps, scroll the map horizontally.</span>}>
          <DataTable paginate={false} className="journey-matrix" aria-label="Customer journey map">
            <thead><tr><th scope="col">From the customer’s point of view</th>{stages.map((stage, index) => <th scope="col" key={index}>
              <span className="journey-stage-number">{fmtNumber(index + 1)}</span><strong>{stage.title}</strong>
              <small>{stage.experience?.evidence_status === "researched" && stage.experience.evidence?.trim() ? "Recorded evidence" : "Needs research"}</small>
            </th>)}</tr></thead>
            <tbody>{mapRows.map(([field, title]) => <tr className={`journey-row-${field}`} key={field}><th scope="row">{title}</th>
              {stages.map((stage, index) => <td key={index}>
                {field === "feeling" && <span className={`journey-emotion ${stage.experience?.emotion ?? "unknown"}`}>{emotions[stage.experience?.emotion ?? "unknown"] ?? emotions.unknown}</span>}
                {stage.experience?.[field] ? <p>{stage.experience[field]}</p> : <span className="journey-unfilled">{field === "evidence" ? "An unregistered source" : "Not registered yet"}</span>}
              </td>)}
            </tr>)}</tbody>
          </DataTable>
        </GridPanel>
        <details className="journey-measurements" key={journey.id}>
          <summary><Icon name="chart" size={16} /> Behavioral data and travel measurements</summary>
          <JourneyMeasurements journey={journey} />
        </details>
      </>}
    </>}
    {editing && <JourneyEditor initial={editing} onClose={() => setEditing(null)} onSaved={id => {
      setSelected(id); setEditing(null); setNotice("The customer journey map is saved."); definitions.reload();
    }} />}
  </>;
}

function JourneyMeasurements({ journey }: { journey: Journey }) {
  const [dates, setDates] = useState(() => { const end = todayIso(); const start = new Date(`${end}T12:00:00Z`); start.setUTCDate(start.getUTCDate() - 29); return { start: start.toISOString().slice(0, 10), end }; });
  const [range, setRange] = useState<{ from: string; to: string } | null>(null);
  const action = useOperation();
  const result = useAsync(() => range ? operations.funnel(journey.id, range.from, range.to) : Promise.resolve(null), [journey.id, range?.from, range?.to]);
  if (journey.map_type === "future_state") return <p className="small muted">This map describes the desired experience; Check the actual data in the current experience map.</p>;
  if (!journey.stages.every(stage => stage.event_names.length > 0)) return <p className="small muted">To measure a path, real events must be attached to all steps. This connection is optional in editing each step; The map can be used without it.</p>;
  return <>
    <p className="small muted">This section only measures the motion recorded between steps. The customer’s thoughts and feelings should be recorded from research evidence.</p>
    <GridFilters as="form" onSubmit={event => { event.preventDefault(); void action.run(async () => setRange(dateRange(dates.start, dates.end))); }}>
      <DatePicker label="Start date" required value={dates.start} onChange={start => setDates({ ...dates, start })} />
      <DatePicker label="End date" required value={dates.end} onChange={end => setDates({ ...dates, end })} />
      <button className="primary">Display data</button>
    </GridFilters>
    {action.errorBox}<ErrorBox error={result.error} />{result.loading && range && <Loading />}
    {result.data && <GridPanel title="Moving between steps" actions={<span className="small muted">The deadline for observing the route: {fmtNumber(journey.window_days)} Day</span>}>
      <DataTable paginate={false} aria-label="Measurement of travel stages"><thead><tr><th>Stage</th><th>Customers arrived</th><th>Continue to the next step</th><th>Still on deadline</th><th>No continuation after the deadline</th><th>Registered Consent</th></tr></thead>
        <tbody>{result.data.stages.map((stage, index) => <tr key={index}><td>{stage.title}</td><td>{fmtNumber(stage.reached)}</td>
          <td>{index === journey.stages.length - 1 ? "The end of the measurement path" : fmtNumber(stage.completed_next)}</td><td>{fmtNumber(stage.pending)}</td><td>{fmtNumber(stage.dropped)}</td>
          <td>{stage.csat == null ? "There is no data" : `${fmtNumber(stage.csat)}%`}</td></tr>)}</tbody>
      </DataTable>
    </GridPanel>}
  </>;
}

function JourneyEditor({ initial, onClose, onSaved }: { initial: Journey; onClose: () => void; onSaved: (id: string) => void }) {
  const [draft, setDraft] = useState(() => normalize(initial));
  const [section, setSection] = useState<"context" | "stages">("context");
  const [index, setIndex] = useState(0);
  const action = useOperation();
  const stage = draft.stages[index];
  const experience = stage.experience!;
  function setStage(patch: Partial<JourneyStage>) { setDraft(current => ({ ...current, stages: current.stages.map((item, position) => position === index ? { ...item, ...patch } : item) })); }
  function setExperience(key: keyof JourneyExperience, value: string) { setStage({ experience: { ...experience, [key]: value } }); }
  function move(offset: number) {
    const stages = [...draft.stages]; [stages[index], stages[index + offset]] = [stages[index + offset], stages[index]];
    setDraft({ ...draft, stages }); setIndex(index + offset);
  }
  async function save() {
    if (!draft.name.trim() || !draft.persona?.trim() || !draft.scenario?.trim() || !draft.goal?.trim()) {
      setSection("context"); throw new Error("Complete the map name, customer group, scenario and target.");
    }
    const missing = draft.stages.findIndex(item => !item.title.trim() || (item.experience?.evidence_status === "researched" && !item.experience.evidence.trim()));
    if (missing >= 0) { setIndex(missing); setSection("stages"); throw new Error(`The title and source of the evidence of the stage ${fmtNumber(missing + 1)} . Returns.`); }
    await operations.saveJourney({ ...draft, stages: draft.stages.map(item => ({ ...item,
      event_names: item.event_names.map(name => name.trim()).filter(Boolean),
    })) }); onSaved(draft.id);
  }

  return <Modal title="Edit the customer journey map" onClose={onClose} wide>
    <form className="journey-editor" onSubmit={event => { event.preventDefault(); void action.run(save); }}>
      <fieldset disabled={action.busy}>
        <div className="tabs" role="group" aria-label="Sections of the map">
          <button type="button" className={section === "context" ? "active" : ""} aria-pressed={section === "context"} onClick={() => setSection("context")}>1. The client and the target of the map</button>
          <button type="button" className={section === "stages" ? "active" : ""} aria-pressed={section === "stages"} onClick={() => setSection("stages")}>2. The experience of each stage</button>
        </div>
        {section === "context" ? <div className="ops-form-grid">
          <OpsField label="Map name"><input maxLength={128} value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })} placeholder="For example, the experience of receiving the first service" /></OpsField>
          <OpsField label="Map type"><select value={draft.map_type} onChange={event => setDraft({ ...draft, map_type: event.target.value as Journey["map_type"] })}><option value="current_state">Current experience</option><option value="future_state">Desired experience</option></select></OpsField>
          <OpsField label="Customer / Persona" wide><textarea maxLength={4000} rows={2} value={draft.persona} onChange={event => setDraft({ ...draft, persona: event.target.value })} placeholder="What group of customers is this map for? What is their main need, motivation and expectation?" /></OpsField>
          <OpsField label="The scenario and scope of travel" wide><textarea maxLength={4000} rows={2} value={draft.scenario} onChange={event => setDraft({ ...draft, scenario: event.target.value })} placeholder="What is the position of the customer? Where does the journey begin and where does it end?" /></OpsField>
          <OpsField label="The goal of research or improvement" wide><textarea maxLength={4000} rows={2} value={draft.goal} onChange={event => setDraft({ ...draft, goal: event.target.value })} placeholder="Knowing this trip, what problem do you want to improve?" /></OpsField>
          <label className="ops-check"><input type="checkbox" checked={draft.enabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })} />Active map</label>
          <button type="button" onClick={() => setSection("stages")}>Continued: Travel stages <Icon name="arrow" size={15} /></button>
        </div> : <>
          <div className="journey-stage-tabs" role="group" aria-label="Step selection">
            {draft.stages.map((item, position) => <button type="button" key={position} className={position === index ? "active" : ""} aria-pressed={position === index} onClick={() => setIndex(position)}>{fmtNumber(position + 1)}. {item.title || "New stage"}</button>)}
            <button type="button" disabled={draft.stages.length >= 12} onClick={() => { setDraft({ ...draft, stages: [...draft.stages, blankStage()] }); setIndex(draft.stages.length); }}><Icon name="plus" size={15} /> Stage</button>
          </div>
          <div className="journey-stage-editor" key={index}>
            <div className="journey-stage-toolbar"><strong>Step {fmtNumber(index + 1)}</strong><div className="toolbar">
              <button type="button" disabled={index === 0} onClick={() => move(-1)}>Earlier</button><button type="button" disabled={index === draft.stages.length - 1} onClick={() => move(1)}>Later</button>
              <button type="button" disabled={draft.stages.length <= 2} onClick={() => { setDraft({ ...draft, stages: draft.stages.filter((_, position) => position !== index) }); setIndex(Math.max(0, index - 1)); }}>Step elimination</button>
            </div></div>
            <div className="ops-form-grid">
              <OpsField label="Step title"><input maxLength={128} value={stage.title} onChange={event => setStage({ title: event.target.value })} placeholder="Name it from the customer’s point of view" /></OpsField>
              <OpsField label="Sentiment"><select value={experience.emotion} onChange={event => setExperience("emotion", event.target.value)}>{Object.entries(emotions).map(([value, title]) => <option value={value} key={value}>{title}</option>)}</select></OpsField>
              {experienceFields.map(([key, title, placeholder]) => <OpsField key={key} label={title}><textarea maxLength={4000} rows={2} value={experience[key] ?? ""} onChange={event => setExperience(key, event.target.value)} placeholder={placeholder} /></OpsField>)}
              <OpsField label="The support of this stage"><select value={experience.evidence_status} onChange={event => setExperience("evidence_status", event.target.value)}><option value="hypothesis">hypothesis; Needs to be checked</option><option value="researched">Based on the recorded evidence</option></select></OpsField>
              <OpsField label="Research evidence and sources" wide><textarea maxLength={4000} rows={3} value={experience.evidence} onChange={event => setExperience("evidence", event.target.value)} placeholder="For example, interview result, customer quote, feedback ID or report link; Separate the observed from the hypothesis." /></OpsField>
            </div>
            <details className="journey-event-connection"><summary>Optional connection to behavioral data</summary>
              <p className="small muted">To count the passage of customers through the steps. Event names do not replace experience descriptions, contact points, or evidence.</p>
              <OpsField label="The name of the events of this stage; Each line is an event"><textarea maxLength={4000} rows={3} dir="ltr" value={stage.event_names.join("\n")}
                onChange={event => setStage({ event_names: event.target.value.split("\n") })} onBlur={() => setStage({ event_names: stage.event_names.map(name => name.trim()).filter(Boolean) })} /></OpsField>
              <OpsField label="The deadline for viewing the complete route (Day)"><input type="number" min={1} max={365} value={draft.window_days} onChange={event => setDraft({ ...draft, window_days: Number(event.target.value) })} /></OpsField>
            </details>
          </div>
        </>}
      </fieldset>
      {action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Save map</button><button type="button" disabled={action.busy} onClick={onClose}>Cancel</button></div>
    </form>
  </Modal>;
}
