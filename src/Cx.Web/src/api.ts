/**
 * API client for the CX console.
 *
 * Authentication is provided by the deployment. The bundled demo uses the backend's
 * local mock identity and therefore does not store or ask for an API key.
 */

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly detail?: unknown,
  ) {
    super(message);
  }
}

export const UNAUTHORIZED_EVENT = "cx:unauthorized";

export interface ReportFilters {
  [key: string]: string | undefined;
  from?: string; to?: string; topics?: string; sentiments?: string; channels?: string;
  severities?: string; types?: string; statuses?: string; q?: string;
  bucket?: string; timeZone?: string; customerId?: string;
}
export interface ReportMetrics {
  total: number; customers: number; analyzed: number; negative: number;
  complaints: number; open_high_severity: number; open: number; resolved: number; overdue: number;
  csat_responses: number; csat_satisfied: number; csat_percent: number | null;
  ces_responses: number; ces_average: number | null; ces_easy_percent: number | null;
  nps_responses: number; nps_score: number | null;
}
export interface ReportGroup extends ReportMetrics { name: string; }
export interface ExperienceReport {
  from: string; to: string; summary: ReportMetrics;
  csat_distribution: { name: string; count: number }[];
  ces_distribution: { name: string; count: number }[];
  trend: ReportGroup[]; topics: ReportGroup[]; sentiments: ReportGroup[];
  channels: ReportGroup[]; severities: ReportGroup[]; types: ReportGroup[]; statuses: ReportGroup[];
  available_channels: string[]; available_topics: string[];
}

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set("Accept", "application/json");
  if (init.body && !(init.body instanceof Blob) && !(init.body instanceof FormData) && !headers.has("Content-Type"))
    headers.set("Content-Type", "application/json");

  const response = await fetch(path, { ...init, headers });

  // An expired session on any call returns the whole console to the sign-in screen.
  if (response.status === 401 && !path.startsWith("/v1/auth/"))
    window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));

  if (response.status === 204) return undefined as T;

  const text = await response.text();
  const payload = text ? safeParse(text) : undefined;

  if (!response.ok) {
    const problem =
      payload && typeof payload === "object"
        ? (payload as Record<string, unknown>)
        : {};
    const validation = Array.isArray(problem.errors)
      ? problem.errors
      : problem.errors && typeof problem.errors === "object"
        ? Object.values(problem.errors).flat()
        : [];
    const message =
      [
        problem.error,
        problem.detail,
        problem.reason,
        validation.filter((value) => typeof value === "string").join(" · "),
        problem.title,
      ].find(
        (value): value is string =>
          typeof value === "string" && value.length > 0,
      ) ?? `${response.status} ${response.statusText}`;
    throw new ApiError(response.status, message, payload);
  }

  if (
    text &&
    (!response.headers.get("Content-Type")?.includes("json") ||
      typeof payload === "string")
  ) {
    throw new ApiError(
      502,
      "The service response is not valid. Check the connection to the server.",
    );
  }
  return payload as T;
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function query(
  params: Record<string, string | number | boolean | undefined | null>,
): string {
  const search = new URLSearchParams();
  for (const [name, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === "") continue;
    search.set(name, String(value));
  }
  const rendered = search.toString();
  return rendered ? `?${rendered}` : "";
}

export interface Page<T> {
  items: T[];
  next_cursor?: string | null;
  has_more: boolean;
}

export const api = {
  health: () => request<{ status: string; roles: string }>("/health"),

  me: () => request<SessionUser>("/v1/auth/me"),
  updateProfile: (body: { display_name: string }) =>
    request<SessionUser>("/v1/auth/profile", { method: "PUT", body: JSON.stringify(body) }),

  login: (username: string, password: string) =>
    request<SessionUser>("/v1/auth/login", {
      method: "POST",
      body: JSON.stringify({ username, password }),
    }),

  logout: () => request<void>("/v1/auth/logout", { method: "POST" }),

  overview: (days: number) =>
    request<Overview>(`/v1/overview${query({ days })}`),

  reports: (params: ReportFilters) => request<ExperienceReport>(`/v1/reports${query(params)}`),

  customers: (params: {
    q?: string;
    segment?: string;
    cursor?: string;
    limit?: number;
  }) => request<Page<CustomerIdentity>>(`/v1/customers${query(params)}`),

  customer: (id: string) =>
    request<CustomerDetail>(`/v1/customers/${encodeURIComponent(id)}`),

  customerAccount: (id: string) =>
    request<{ exists: boolean; username: string | null; enabled: boolean; role: "customer" }>(
      `/v1/customers/${encodeURIComponent(id)}/portal-account`,
    ),

  saveCustomerAccount: (id: string, body: { username: string; password?: string; enabled: boolean }) =>
    request<void>(`/v1/customers/${encodeURIComponent(id)}/portal-account`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),

  createCustomer: (body: CustomerInput) =>
    request<CustomerIdentity>("/v1/customers", {
      method: "POST",
      body: JSON.stringify(body),
    }),

  updateCustomer: (id: string, body: CustomerInput) =>
    request<CustomerIdentity>(`/v1/customers/${encodeURIComponent(id)}`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),

  timeline: (
    id: string,
    params: {
      kinds?: string;
      cursor?: string;
      limit?: number;
      severity?: string;
    },
  ) =>
    request<Page<TimelineEntry>>(
      `/v1/customers/${encodeURIComponent(id)}/timeline${query(params)}`,
    ),

  insights: (id: string) =>
    request<CustomerInsights>(
      `/v1/customers/${encodeURIComponent(id)}/insights`,
    ),

  events: (params: Record<string, string | number | undefined>) =>
    request<Page<CxEvent>>(`/v1/events${query(params)}`),

  feedback: (params: Record<string, string | number | boolean | undefined>) =>
    request<Page<Feedback>>(`/v1/feedback${query(params)}`),

  submitComplaint: (body: { submission_id: string; customer_id: string; category: string; revision: number; comment: string }) =>
    request<{ event_id: string; status: string }>("/v1/complaints", {
      method: "POST", body: JSON.stringify(body),
    }),

  feedbackGrid: (params: FeedbackGridParams) =>
    request<FeedbackGridPage>(`/v1/feedback/grid${query({ ...params })}`),

  feedbackDetail: (id: string) =>
    request<{
      feedback: Feedback;
      analysis?: AiAnalysis | null;
      customer_name?: string | null;
      submission?: { receipt: string; category_title: string; name?: string | null; contact?: string | null } | null;
    }>(`/v1/feedback/${encodeURIComponent(id)}`),

  followUpTask: (id: string) =>
    request<FollowUpTaskDraft>(`/v1/feedback/${encodeURIComponent(id)}/follow-up-task`, {
      method: "POST",
    }),

  reanalyzeFeedback: (id: string) =>
    request<{ status: string; scope: string }>(
      `/v1/feedback/${encodeURIComponent(id)}/reanalyze`,
      { method: "POST" },
    ),

  uploadRecording: (callId: string, recordingId: string, file: File) =>
    request<{ recording_id: string; storage_state: string }>(
      `/v1/voice/recordings${query({ callId, recordingId, language: "en" })}`,
      {
        method: "POST",
        body: file,
        headers: { "Content-Type": file.type || "application/octet-stream" },
      },
    ),

  supportCases: (params: Record<string, string | number | undefined>) =>
    request<Page<SupportCase>>(`/v1/support/cases${query(params)}`),

  voiceCalls: (params: Record<string, string | number | undefined>) =>
    request<Page<VoiceCall>>(`/v1/voice/calls${query(params)}`),

  voiceCall: (callId: string) =>
    request<VoiceCallDetail>(`/v1/voice/calls/${encodeURIComponent(callId)}`),

  transcript: (callId: string) =>
    request<Transcript>(
      `/v1/voice/calls/${encodeURIComponent(callId)}/transcript`,
    ),

  audioUrl: (callId: string) =>
    request<{
      url: string;
      expires_at: string;
      content_type?: string;
      can_download: boolean;
    }>(`/v1/voice/calls/${encodeURIComponent(callId)}/audio`),

  reprocessCall: (callId: string, body: { scope: string; reason?: string }) =>
    request<{ status: string }>(
      `/v1/voice/calls/${encodeURIComponent(callId)}/reprocess`,
      {
        method: "POST",
        body: JSON.stringify(body),
      },
    ),

  editSegment: (
    transcriptId: string,
    index: number,
    body: { text: string; reason?: string },
  ) =>
    request<unknown>(
      `/v1/voice/transcripts/${transcriptId}/segments/${index}`,
      {
        method: "PATCH",
        body: JSON.stringify(body),
      },
    ),

  topics: () => request<TopicTree>("/v1/admin/topics"),

  createTopic: (body: TopicInput) =>
    request<TopicNode>("/v1/admin/topics", {
      method: "POST",
      body: JSON.stringify(body),
    }),

  updateTopic: (id: string, body: TopicInput) =>
    request<TopicNode>(`/v1/admin/topics/${id}`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),

  deleteTopic: (id: string) =>
    request<{ deleted: boolean; deactivated: boolean; usage_count: number }>(
      `/v1/admin/topics/${id}`,
      { method: "DELETE" },
    ),

  pipelines: () => request<PipelineHealth>("/v1/admin/pipelines"),

  integrations: () => request<IntegrationsView>("/v1/admin/integrations"),

  audit: (params: Record<string, string | number | undefined>) =>
    request<Page<AuditEntry>>(`/v1/admin/audit${query(params)}`),

  search: (q: string) =>
    request<{ hits: SearchHit[] }>(`/v1/search${query({ q })}`),
};

// ---------------------------------------------------------------------------
// Types (mirror the API contracts)
// ---------------------------------------------------------------------------

export interface NamedCount {
  name: string;
  count: number;
}

export interface NpsSummary {
  total: number;
  promoters: number;
  passives: number;
  detractors: number;
  promoter_percent: number | null;
  passive_percent: number | null;
  detractor_percent: number | null;
  score: number | null;
}

export interface Overview {
  from: string;
  to: string;
  total_interactions: number;
  active_customers: number;
  feedback_count: number;
  nps: NpsSummary;
  support_cases: number;
  call_count: number;
  negative_sentiment_share: number;
  unresolved_high_severity: number;
  ai_review_backlog: number;
  data_quality_warnings: number;
  top_pain_points: NamedCount[];
  top_affected_products: NamedCount[];
  sentiment_breakdown: NamedCount[];
  daily_trend: {
    date: string;
    events: number;
    negative: number;
    calls: number;
  }[];
}

export interface CustomerIdentity {
  customer_id: string;
  business_id?: string | null;
  display_name?: string | null;
  segment?: string | null;
  phone?: string | null;
  email?: string | null;
  notes?: string | null;
  source?: string | null;
  feedback_count: number;
  negative_count: number;
  first_seen_at: string;
  last_seen_at: string;
  created_at: string;
}

export interface CustomerInput {
  customer_id?: string;
  display_name: string;
  phone?: string;
  email?: string;
  business_id?: string;
  segment?: string;
  notes?: string;
}

export interface FeedbackGridParams {
  inbox?: "feedback" | "complaints";
  channel?: string;
  q?: string;
  status?: string;
  sentiment?: string;
  severity?: string;
  category?: string;
  subcategory?: string;
  kind?: string;
  analysis?: string;
  customerId?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export interface FeedbackGridRow {
  id: string;
  kind: "text" | "voice";
  customer_id?: string | null;
  customer_name?: string | null;
  comment?: string | null;
  channel?: string | null;
  product_id?: string | null;
  feedback_type: string;
  rating?: number | null;
  nps_score?: number | null;
  csat_score?: number | null;
  ces_score?: number | null;
  status: string;
  reviewed: boolean;
  submitted_at: string;
  call_id?: string | null;
  analysis_state: "analyzed" | "pending" | "failed";
  analysis_id?: string | null;
  analysis_provider?: string | null;
  analysis_error?: string | null;
  sentiment?: string | null;
  severity?: string | null;
  category?: string | null;
  subcategory?: string | null;
  summary?: string | null;
  confidence?: number | null;
  human_reviewed: boolean;
}

export interface FeedbackGridPage {
  items: FeedbackGridRow[];
  total: number;
  page: number;
  page_size: number;
  status_counts?: Record<string, number>;
}

export interface CustomerDetail {
  customer: CustomerIdentity;
  open_support_cases: number;
  latest_analysis?: AiAnalysis | null;
  recent_calls: VoiceCall[];
  recent_feedback: Feedback[];
}

export interface TimelineEntry {
  kind: string;
  id: string;
  occurred_at: string;
  title: string;
  detail?: string | null;
  channel?: string | null;
  product_id?: string | null;
  severity?: string | null;
  sentiment?: string | null;
  status?: string | null;
  reviewed: boolean;
}

export interface CustomerInsights {
  customer_id: string;
  current_sentiment?: string | null;
  dominant_intent?: string | null;
  journey_stage?: string | null;
  latest_summary?: string | null;
  recurring_issues: NamedCount[];
  unresolved_issues: number;
  open_support_cases: number;
  last_interaction_at?: string | null;
  human_reviewed_labels: NamedCount[];
}

export interface CxEvent {
  event_id: string;
  event_name: string;
  event_category: string;
  occurred_at: string;
  customer_id?: string | null;
  product_id?: string | null;
  source_service?: string | null;
  correlation_id?: string | null;
}

export interface Feedback {
  id: string;
  selected_topic?: string | null;
  customer_id?: string | null;
  product_id?: string | null;
  feedback_type: string;
  rating?: number | null;
  nps_score?: number | null;
  csat_score?: number | null;
  ces_score?: number | null;
  comment?: string | null;
  channel?: string | null;
  call_id?: string | null;
  status: string;
  reviewed: boolean;
  submitted_at: string;
}

export interface SupportCase {
  id: string;
  external_id: string;
  customer_id?: string | null;
  subject?: string | null;
  status: string;
  priority?: string | null;
  escalated: boolean;
  opened_at: string;
  resolved_at?: string | null;
}

export interface VoiceCall {
  inbox: "feedback" | "complaints";
  id: string;
  call_id: string;
  customer_id?: string | null;
  agent_id?: string | null;
  direction?: string | null;
  started_at?: string | null;
  ended_at?: string | null;
  duration_ms?: number | null;
  end_reason?: string | null;
  lifecycle_state: string;
}

export interface VoiceRecordingView {
  id: string;
  storage_state: string;
  content_type?: string | null;
  duration_ms?: number | null;
  size_bytes?: number | null;
  checksum_sha256?: string | null;
  verified_at?: string | null;
}

export interface VoiceCallDetail {
  call: VoiceCall;
  recordings: VoiceRecordingView[];
  analysis?: AiAnalysis | null;
}

export interface TranscriptSegment {
  index: number;
  speaker: string;
  start_ms: number;
  end_ms: number;
  text: string;
  confidence?: number | null;
}

export interface Transcript {
  transcript_id: string;
  recording_id: string;
  language?: string | null;
  engine: string;
  model: string;
  average_confidence?: number | null;
  created_at: string;
  redacted: boolean;
  text?: string | null;
  segments: TranscriptSegment[];
}

export interface AnalysisResult {
  sentiment: string;
  intent: string;
  category: string;
  subcategory?: string | null;
  severity: string;
  journey_stage?: string | null;
  resolution_status: string;
  summary: string;
  action_items: string[];
  mentioned_products: string[];
  confidence: number;
  call_reason?: string | null;
  follow_up_required?: boolean | null;
  key_moments?:
    | { start_ms: number; end_ms: number; type: string; summary: string }[]
    | null;
}

export interface AiAnalysis {
  id: string;
  entity_type: string;
  entity_id: string;
  analysis_type: string;
  model_provider: string;
  model_name: string;
  model_version?: string | null;
  prompt_version?: string | null;
  taxonomy_version?: string | null;
  confidence?: number | null;
  status: string;
  created_at: string;
  supersedes_analysis_id?: string | null;
  result: AnalysisResult | string;
}

export interface SessionUser {
  username: string;
  display_name: string;
  tenant_id: string;
  /** "login" when the operator signed in; "mock" / "apikey" / "jwt" otherwise. */
  mode: string;
  permissions: string[];
}

/** Draft task for the technical team; `text` is ready to paste into their tracker. */
export interface FollowUpTaskDraft {
  title: string;
  priority: string;
  text: string;
  generated_by: "ai" | "template";
  model?: string | null;
}

/** One node of the operator-maintained topic tree the AI classifies feedback into. */
export interface TopicNode {
  id: string;
  parent_id?: string | null;
  /** Generated key stored on AI results; never edited by operators. */
  code: string;
  title: string;
  description?: string | null;
  keywords: string[];
  sort_order: number;
  is_active: boolean;
  usage_count: number;
  updated_by?: string | null;
  updated_at: string;
}

export interface TopicTree {
  revision: number;
  decision_model?: string | null;
  can_manage: boolean;
  topics: TopicNode[];
}

export interface TopicInput {
  parent_id?: string | null;
  title: string;
  description?: string | null;
  keywords: string[];
  sort_order?: number;
  is_active: boolean;
}

export interface PipelineHealth {
  outbox_backlog: number;
  outbox_lag_seconds?: number | null;
  events_last_hour: number;
  events_last_24h: number;
  review_backlog: number;
  jobs_by_state: Record<string, number>;
  recordings_by_state: Record<string, number>;
  ai_analyses_last_24h: number;
  transcripts_last_24h: number;
  failed_jobs: {
    job_key: string;
    job_type: string;
    state: string;
    attempts: number;
    error_category?: string | null;
    last_error?: string | null;
    updated_at: string;
  }[];
}

export interface IntegrationsView {
  checkpoints: {
    integration_name: string;
    integration_type: string;
    status: string;
    last_success_at?: string | null;
    last_error?: string | null;
    records_processed: number;
  }[];
  live: {
    name: string;
    type: string;
    healthy: boolean;
    detail?: string | null;
  }[];
}

export interface AuditEntry {
  id: number;
  actor_id: string;
  action: string;
  resource_type: string;
  resource_id: string;
  reason?: string | null;
  occurred_at: string;
  before?: string | null;
  after?: string | null;
}

export interface SearchHit {
  kind: string;
  id: string;
  label: string;
  detail?: string | null;
  customer_id?: string | null;
  occurred_at?: string | null;
}

export function parseResult(
  analysis: AiAnalysis | null | undefined,
): AnalysisResult | null {
  if (!analysis) return null;
  if (typeof analysis.result === "string") {
    try {
      return JSON.parse(analysis.result) as AnalysisResult;
    } catch {
      return null;
    }
  }
  return analysis.result;
}
