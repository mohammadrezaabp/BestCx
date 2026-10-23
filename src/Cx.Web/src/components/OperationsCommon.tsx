import { useState, type ReactNode } from "react";
import { ErrorBox } from "./Common";
import type { Directory } from "../operationsApi";
import { severityLabels } from "../format";

export const caseStatuses: Record<string, string> = { new: "New", in_progress: "In progress", waiting_customer: "Awaiting customer reply", resolved: "Resolved; awaiting confirmation", closed: "Closed" };
export const staffRoles: Record<string, string> = { admin: "Administrator", cx_lead: "CX manager", support_supervisor: "Support supervisor", cx_analyst: "Agent", data_ai_operator: "Data and AI operator" };
export function OpsField({ label, children, wide }: { label: string; children: ReactNode; wide?: boolean }) {
  return <label className={`grid-filter${wide ? " ops-wide" : ""}`}><span>{label}</span>{children}</label>;
}
export function OpsSelect({ label, value, onChange, options, empty }: { label: string; value: string; onChange: (value: string) => void; options: Record<string, string>; empty?: string }) {
  return <OpsField label={label}><select aria-label={label} value={value} onChange={event => onChange(event.target.value)}>
    {empty !== undefined && <option value="">{empty}</option>}
    {Object.entries(options).map(([code, name]) => <option value={code} key={code}>{name}</option>)}
  </select></OpsField>;
}
export function PrioritySelect({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  return <OpsSelect label="Severity" value={value} onChange={onChange} options={severityLabels} />;
}
export function AssignmentFields({ directory, team, assignee, onTeam, onAssignee, disabled = false }: {
  directory: Directory; team: string; assignee: string; onTeam: (value: string) => void; onAssignee: (value: string) => void; disabled?: boolean;
}) {
  const currentTeam = directory.teams.find(item => item.id === team);
  const staff = directory.staff.filter(item => item.enabled && item.role !== "data_ai_operator" && (!currentTeam || currentTeam.members.includes(item.id)));
  return <>
    <OpsField label="Team"><select disabled={disabled} value={team} onChange={event => { onTeam(event.target.value); onAssignee(""); }}>
      <option value="">No team</option>{directory.teams.filter(item => item.enabled || item.id === team).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select></OpsField>
    <OpsField label="Assignee"><select disabled={disabled} value={assignee} onChange={event => onAssignee(event.target.value)}>
      <option value="">Unassigned</option>{staff.map(item => <option key={item.id} value={item.id}>{item.display_name || item.username}</option>)}
      {assignee && !staff.some(item => item.id === assignee) && <option value={assignee}>Previous official (Disabled)</option>}
    </select></OpsField>
  </>;
}
export function useOperation() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const run = async (action: () => Promise<unknown>) => {
    if (busy) return;
    setBusy(true); setError(null);
    try { await action(); } catch (err) { setError(err); } finally { setBusy(false); }
  };
  return { busy, error, run, errorBox: <ErrorBox error={error} /> };
}
export function dateRange(from: string, to: string) {
  if (!from || !to || from > to) throw new Error("The date range is not valid.");
  const start = new Date(`${from}T00:00:00+00:00`);
  const end = new Date(`${to}T00:00:00+00:00`); end.setUTCDate(end.getUTCDate() + 1);
  if ((end.getTime() - start.getTime()) / 86400000 > 366) throw new Error("The maximum interval is 366 days.");
  return { from: start.toISOString(), to: end.toISOString() };
}
