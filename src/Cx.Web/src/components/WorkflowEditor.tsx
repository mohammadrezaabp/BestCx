import { useState } from "react";
import { operations, type Directory, type Workflow, type WorkflowStage } from "../operationsApi";
import { OpsField, OpsSelect, useOperation } from "./OperationsCommon";
import Modal from "./Modal";
import TopicSelects, { rootTopicCode } from "./TopicSelects";
import { useTopics } from "../hooks/useTopics";
import { fmtNumber } from "../format";

export default function WorkflowEditor({ value, directory, onClose, onSaved }: {
  value: Workflow; directory: Directory; onClose: () => void; onSaved: () => void;
}) {
  const [draft, setDraft] = useState(value);
  const topics = useTopics();
  const action = useOperation();
  const root = rootTopicCode(topics.options, draft.topic ?? "") ?? draft.topic ?? "";
  const teams = Object.fromEntries(directory.teams.map(team => [team.id, `${team.name}${team.enabled ? "" : " (Disabled)"}`]));
  function patch(index: number, change: Partial<WorkflowStage>) {
    setDraft(current => ({ ...current, stages: current.stages.map((stage, position) => position === index ? { ...stage, ...change } : stage) }));
  }
  function move(index: number, delta: number) {
    const stages = [...draft.stages];
    [stages[index], stages[index + delta]] = [stages[index + delta], stages[index]];
    setDraft({ ...draft, stages });
  }
  return <Modal title="Workflow" onClose={onClose} wide><form onSubmit={event => { event.preventDefault(); void action.run(async () => {
    await operations.saveWorkflow(draft); onSaved();
  }); }}>
    <fieldset className="ops-form-grid" disabled={action.busy}>
      <OpsField label="Name of the procedure" wide><input required maxLength={128} value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })} /></OpsField>
      <OpsField label="Description of the procedure for intelligent selection" wide><textarea rows={3} maxLength={4000} value={draft.description} onChange={event => setDraft({ ...draft, description: event.target.value })} /></OpsField>
      <TopicSelects options={topics.options} parentValue={root} childValue={root !== draft.topic ? draft.topic ?? "" : ""}
        onParentChange={value => setDraft({ ...draft, topic: value || null })} onChildChange={value => setDraft({ ...draft, topic: value || root || null })} />
      <label className="ops-check ops-wide"><input type="checkbox" checked={draft.enabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })} />Active procedure</label>
      <div className="ops-wide workflow-editor-stages">{draft.stages.map((stage, index) => <fieldset key={index} className="workflow-editor-stage">
        <legend>Stage {fmtNumber(index + 1)}</legend>
        <div className="ops-form-grid">
          <OpsField label="Step title"><input required maxLength={128} value={stage.title} onChange={event => patch(index, { title: event.target.value })} /></OpsField>
          <OpsSelect label="The team responsible for the stage" value={stage.team_id ?? ""} options={teams} empty="Smart team selection" onChange={value => patch(index, { team_id: value || null })} />
          <OpsField label="Step agenda" wide><textarea rows={2} maxLength={4000} value={stage.description} onChange={event => patch(index, { description: event.target.value })} /></OpsField>
        </div>
        <div className="form-submit">
          <button type="button" disabled={index === 0} onClick={() => move(index, -1)}>Move up</button>
          <button type="button" disabled={index === draft.stages.length - 1} onClick={() => move(index, 1)}>Move down</button>
          <button type="button" className="danger" disabled={draft.stages.length === 1} onClick={() => setDraft({ ...draft, stages: draft.stages.filter((_, position) => position !== index) })}>Step elimination</button>
        </div>
      </fieldset>)}</div>
      <div className="ops-wide"><button type="button" disabled={draft.stages.length >= 20} onClick={() => setDraft({ ...draft, stages: [...draft.stages, { title: "", team_id: null, description: "" }] })}>Add step</button></div>
    </fieldset>
    <p className="small muted">The procedure change is applied to new files; Processes of ongoing cases are maintained.</p>
    {action.errorBox}<div className="form-submit"><button className="primary" disabled={action.busy}>Save workflow</button><button type="button" onClick={onClose}>Cancel</button></div>
  </form></Modal>;
}
