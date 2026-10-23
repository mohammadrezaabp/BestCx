import { request } from "./api";

export interface Staff { id: string; username: string; display_name: string; role: string; enabled: boolean; }
export interface Team { id: string; name: string; description?: string; lead_id: string | null; members: string[]; enabled: boolean; }
export interface WorkflowStage { title: string; team_id: string | null; description: string; }
export interface Workflow { id: string; name: string; description: string; topic: string | null; stages: WorkflowStage[]; enabled: boolean; version: number; }
export interface Directory { staff: Staff[]; teams: Team[]; current_staff_id: string | null; }
export interface Calendar { time_zone: string; working_days: number[]; starts_at: string; ends_at: string; holidays: string[]; }
export interface Policy { id: string; name: string; priority: string; response_minutes: number; resolution_minutes: number; calendar: Calendar; enabled: boolean; }
export interface WorkCase {
  id: string; reference: string; customer_id: string; customer_name: string; title: string; description: string;
  topic: string | null; priority: string; status: string; team_id: string | null; team_name: string | null;
  assignee_id: string | null; assignee_name: string | null; problem_id: string | null; problem_title: string | null;
  response_due_at: string; resolution_due_at: string; first_response_at: string | null; resolved_at: string | null;
  response_overdue: boolean; resolution_overdue: boolean; pause_started_at: string | null;
  resolution_text: string; reopen_count: number; created_at: string; updated_at: string; version: number;
  workflow_id?: string | null; workflow?: { name: string; stages: WorkflowStage[] }; stage_index?: number;
  routing_reason?: string; routing_model?: string | null;
}
export interface CaseInput { id: string; customer_id: string; title: string; description: string; topic?: string | null;
  priority: string; team_id: string | null; assignee_id: string | null; problem_id?: string | null; feedback_id?: string | null; }
export interface CaseUpdate { version: number; status: string; team_id: string | null; assignee_id: string | null;
  problem_id: string | null; priority: string; resolution_text: string; comment?: string; }
export interface Activity { id: string; actor: string; kind: string; public: boolean; text: string; created_at: string; team_id?: string | null; stage_title?: string | null; }
export interface Attachment { id: string; filename: string; size_bytes: number; public: boolean; uploaded_by: string; created_at: string; }
export interface CaseDetail { case: WorkCase; activities: Activity[]; attachments: Attachment[]; feedback_ids: string[]; }
export interface CasePage { items: WorkCase[]; total: number; page: number; page_size: number; }
export interface Problem { id: string; title: string; topic: string | null; root_cause: string; improvement: string; status: string;
  owner_id: string | null; version: number; fixed_at: string | null; created_at: string; case_count: number; affected_customers: number;
  before_count: number; after_count: number; comparison_days: number; }
export interface Rule { id: string; name: string; topic: string | null; severity: string | null; channel: string | null;
  repeat_count: number; priority: string; team_id: string | null; assignee_id: string | null; create_case: boolean; notify: boolean; enabled: boolean; }
export interface Execution { rule_id: string; rule_name: string; feedback_id: string; case_id: string | null; state: string; error: string | null; executed_at: string; }
export interface Campaign { id: string; name: string; question: string; metric: string; trigger: string; event_name: string | null;
  segment: string | null; cooldown_days: number; reminder_days: number; expires_days: number; enabled: boolean;
  invitations: number; responses: number; declined: number; expired: number; average_score: number | null; }
export interface JourneyExperience {
  goal: string; actions: string; touchpoints: string; thoughts: string; emotion: string; feeling: string;
  pain_points: string; opportunities: string; evidence: string; evidence_status: string; success_metric: string; owner: string;
}
export interface JourneyStage { title: string; event_names: string[]; experience?: JourneyExperience; }
export interface Journey {
  id: string; name: string; window_days: number; enabled: boolean; stages: JourneyStage[];
  persona?: string; scenario?: string; goal?: string; map_type?: "current_state" | "future_state";
}
export interface JourneyResult { id: string; name: string; window_days: number; stages: { title: string; reached: number;
  completed_next: number; pending: number; dropped: number; csat: number | null; complaints: number; }[]; }
export interface FollowUpCustomer { customer_id: string; display_name: string; segment: string | null;
  complaints: number; open_cases: number; overdue_cases: number; follow_up_score: number; reasons: string[]; }
export interface Notification { id: string; title: string; body: string; link: string; created_at: string; read_at: string | null; }
export interface NotificationPage { items: Notification[]; total: number; page: number; page_size: number; }
export interface Outcome { name: string; total: number; resolved: number; overdue: number; reopened: number;
  response_measured: number; resolution_measured: number; response_within_sla: number; resolution_within_sla: number;
  average_response_minutes: number | null; average_resolution_minutes: number | null; aftercare_csat: number | null; aftercare_responses: number; }
export interface OutcomeReport { summary: Outcome; teams: Outcome[]; topics: Outcome[]; }

const base = "/v1/operations";
const get = <T>(path: string) => request<T>(base + path);
const send = <T>(path: string, body: unknown, method = "PUT") => request<T>(base + path, { method, body: JSON.stringify(body) });
function params(values: Record<string, string | number | boolean | null | undefined>) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "" && value !== false) query.set(key, String(value));
  return query.size ? `?${query}` : "";
}
export const operations = {
  directory: () => get<Directory>("/directory"),
  cases: (filters: Record<string, string | number | boolean | null | undefined>) => get<CasePage>("/cases" + params(filters)),
  detail: (id: string) => get<CaseDetail>(`/cases/${id}`),
  forFeedback: (id: string) => get<{ id: string | null }>(`/cases/by-feedback/${id}`),
  create: (input: CaseInput) => send<{ id: string }>("/cases", input, "POST"),
  update: (id: string, input: CaseUpdate) => send<void>(`/cases/${id}`, input),
  workflow: (id: string, version: number, action: "start" | "next" | "close", text = "") => send<{ can_access: boolean }>(`/cases/${id}/workflow`, { version, action, text }, "POST"),
  workflows: () => get<Workflow[]>("/workflows"),
  saveWorkflow: (input: Workflow) => send<void>("/workflows", input),
  message: (id: string, text: string, isPublic: boolean, messageId: string) => send<void>(`/cases/${id}/messages`, { id: messageId, text, public: isPublic }, "POST"),
  upload: (id: string, file: File, isPublic: boolean, attachmentId: string) => {
    const body = new FormData(); body.append("file", file); body.append("public", String(isPublic)); body.append("attachment_id", attachmentId);
    return request<Attachment>(`${base}/cases/${id}/attachments`, { method: "POST", body });
  },
  notifications: () => get<Notification[]>("/notifications"),
  notificationGrid: (filters: Record<string, string | number>) => get<NotificationPage>("/notifications/grid" + params(filters)),
  unreadNotifications: () => get<{ count: number }>("/notifications/unread-count"),
  readNotification: (id: string) => send<void>(`/notifications/${id}/read`, {}, "POST"),
  problems: () => get<Problem[]>("/problems"),
  saveProblem: (input: Partial<Problem>) => send<void>("/problems", input),
  policies: () => get<Policy[]>("/policies"),
  savePolicy: (input: Policy) => send<void>("/policies", input),
  rules: () => get<Rule[]>("/rules"),
  saveRule: (input: Rule) => send<void>("/rules", input),
  executions: () => get<Execution[]>("/executions"),
  saveTeam: (input: Team) => send<void>("/teams", input),
  saveStaff: (input: Staff & { password?: string }) => send<void>("/staff", input),
  campaigns: () => get<Campaign[]>("/campaigns"),
  saveCampaign: (input: Partial<Campaign>) => send<void>("/campaigns", input),
  dispatch: (id: string, customers: string[]) => send<{ invited: number; skipped: number }>(`/campaigns/${id}/dispatch`, { customers }, "POST"),
  journeys: () => get<Journey[]>("/journeys"),
  saveJourney: (input: Journey) => send<void>("/journeys", input),
  funnel: (id: string, from: string, to: string) => get<JourneyResult>(`/journeys/${id}/funnel` + params({ from, to })),
  followUp: (segment: string, minScore: number) => get<FollowUpCustomer[]>("/follow-up-customers" + params({ segment, minScore })),
  outcomes: (from: string, to: string) => get<OutcomeReport>("/outcomes" + params({ from, to })),
};
