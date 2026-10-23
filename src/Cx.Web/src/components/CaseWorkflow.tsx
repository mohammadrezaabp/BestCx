import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { operations, type Directory, type WorkCase } from "../operationsApi";
import { OpsField, useOperation } from "./OperationsCommon";

export default function CaseWorkflow({ item, directory, canManage, manager, onChanged }: {
  item: WorkCase; directory: Directory | null; canManage: boolean; manager: boolean; onChanged: () => void;
}) {
  const navigate = useNavigate();
  const action = useOperation();
  const [text, setText] = useState("");
  const stages = item.workflow?.stages ?? [];
  const index = item.stage_index ?? -1;
  const started = index >= 0 && stages.length > 0;
  const open = item.status !== "closed" && item.status !== "resolved";
  const ownTeam = !item.team_id || directory?.teams.some(team => team.id === item.team_id && team.members.includes(directory.current_staff_id ?? ""));
  const canAct = canManage && (manager || ownTeam);
  async function transition(kind: "start" | "next" | "close") {
    await action.run(async () => {
      const result = await operations.workflow(item.id, item.version, kind, text.trim());
      setText("");
      if (result.can_access === false) navigate("/complaints/cases", { replace: true, state: { notice: "The case was referred to the team in charge of the stage." } });
      else onChanged();
    });
  }
  return <section className="card case-workflow" aria-label="Case workflow">
    {started && <>
      <h2>{item.workflow!.name}</h2>
      <ol className="case-workflow-stages">{stages.map((stage, position) => <li key={position}
        className={position < index || !open && position === index ? "completed" : position === index ? "current" : ""}
        aria-current={open && position === index ? "step" : undefined}>
        <strong>{stage.title}</strong><span>{directory?.teams.find(team => team.id === stage.team_id)?.name ?? "Responsible team"}</span>
        {position === index && stage.description && <small>{stage.description}</small>}
      </li>)}</ol>
    </>}
    {item.routing_reason && <details><summary>The reason for choosing the route</summary><p className="small muted">{item.routing_reason}</p></details>}
    {open && canAct && <>
      {!started && <button type="button" className="primary" disabled={action.busy || !directory} onClick={() => void transition("start")}>
        {action.busy ? "Choosing a team and routine…" : "Start case handling"}
      </button>}
      {(started || item.status !== "new") && <details className="case-transition"><summary>Record the result and change the stage</summary><div>
        <OpsField label="Team notes / Resolution"><textarea rows={3} maxLength={12000} value={text}
          placeholder="Write down the action taken and the result of the team review…" onChange={event => setText(event.target.value)} disabled={action.busy} /></OpsField>
        <div className="form-submit">
          {started && index + 1 < stages.length && <button type="button" className="primary" disabled={action.busy || !text.trim()} onClick={() => void transition("next")}>Next stage</button>}
          <button type="button" className="primary" disabled={action.busy || !text.trim()} onClick={() => void transition("close")}>Resolve case</button>
        </div>
        <p className="small muted">The description of the internal stage; At the end of the process, the result is also recorded for the customer.</p>
      </div></details>}
    </>}
    {!open && <p className="small muted">The proceedings are over.</p>}
    {open && !canAct && <p className="small muted">It is the responsibility of the responsible team to register the explanation and change the stage.</p>}
    {action.errorBox}
  </section>;
}
