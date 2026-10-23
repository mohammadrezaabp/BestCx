/** English formatting helpers. The panel is timezone-aware and LTR (panel proposal 18). */

const dateTime = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

const dateOnly = new Intl.DateTimeFormat("en-US", { dateStyle: "medium" });
const timeOnly = new Intl.DateTimeFormat("en-US", { timeStyle: "short" });
const numbers = new Intl.NumberFormat("en-US");

export function fmtDateTime(value?: string | null): string {
  if (!value) return "—";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? "—" : dateTime.format(parsed);
}

export function fmtDate(value?: string | null): string {
  if (!value) return "—";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? "—" : dateOnly.format(parsed);
}

export function fmtTime(value?: string | null): string {
  if (!value) return "—";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? "—" : timeOnly.format(parsed);
}

export function fmtNumber(value?: number | null): string {
  return value === undefined || value === null ? "—" : numbers.format(value);
}

export function fmtPercent(value?: number | null): string {
  return value === undefined || value === null
    ? "—"
    : `${numbers.format(Math.round(value * 100))}%`;
}

/** mm:ss for the audio player and transcript timestamps. */
export function fmtClock(ms?: number | null): string {
  if (ms === undefined || ms === null || Number.isNaN(ms)) return "00:00";
  const total = Math.max(0, Math.floor(ms / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

export function fmtBytes(bytes?: number | null): string {
  if (!bytes) return "—";
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

export const sentimentLabels: Record<string, string> = {
  positive: "Positive",
  neutral: "Neutral",
  negative: "Negative",
  mixed: "Mixed",
  unknown: "Unknown",
};

export const severityLabels: Record<string, string> = {
  low: "Low",
  medium: "Medium",
  high: "High",
  critical: "Critical",
};

export const resolutionLabels: Record<string, string> = {
  resolved: "Resolved",
  partially_resolved: "Partially resolved",
  unresolved: "Unresolved",
  unknown: "Unknown",
};

export const reviewReasonLabels: Record<string, string> = {
  low_confidence: "Low confidence",
  critical_severity: "Critical intensity",
  category_not_in_taxonomy: "Uncategorized topic",
  policy_required: "Policy requirement",
  qa_sample: "Sample quality control",
};

export const statusLabels: Record<string, string> = {
  unknown: "Unknown",
  customer: "Customer",
  event: "Event",
  voice: "Audio",
  transcript: "Transcript",
  mobile: "Mobile app",
  new: "New",
  in_progress: "In progress",
  reviewed: "Reviewed",
  closed: "Closed",
  open: "Open",
  resolved: "Resolved",
  escalated: "Escalated",
  feedback: "Feedback",
  complaint: "Complaint",
  voc: "Customer survey",
  rating: "Rating",
  nps: "Loyalty",
  csat: "Satisfaction",
  support: "Support",
  voice_call: "Voice call",
  cx_event: "Customer interaction",
  comment: "Comment",
  inbound: "Inbound",
  outbound: "Outbound",
  internal: "Internal",
  web: "Website",
  phone: "Tel",
  email: "Email",
  manual: "Manual entry",
  in_person: "In person",
  social: "Social network",
  generic: "Comment",
  analyzed: "Analyzed",
  pending: "Pending analysis",
  failed: "Analysis failed",
  ENDED: "Finished",
  READY_FOR_TRANSCRIPTION: "Ready for transcription",
  TRANSCRIBED: "Transcribed",
  ANALYZED: "Analyzed",
  REVIEWED: "Reviewed",
  STARTED: "Started",
  ANSWERED: "Answered",
  RECORDING_STAGING: "Receiving audio",
  RECORDING_STORED: "Sound saved",
  RECORDING_VERIFIED: "Voice confirmed",
};
/** Labels for topic codes stored before topics moved to the database; live topics come from useTopics. */
export const topicLabels: Record<string, string> = {
  billing: "Finance and billing",
  onboarding: "Start of work",
  product_usage: "Use of the product",
  support_quality: "Support quality",
  other: "Others",
  authentication: "Login and authentication",
  integration: "Connection of services",
  payment: "Payment",
  payments: "Payment",
  performance: "Speed and performance",
  payment_failure: "Payment error",
  settlement_latency: "Delay in settlement",
  settlement: "Settlement",
  identity: "Authentication",
  support: "Support",
  general: "General",
};

export const reviewStatusLabels: Record<string, string> = {
  PENDING: "Waiting",
  APPROVED: "Confirmed",
  OVERRIDDEN: "Rewritten",
  REJECTED: "Rejected",
};

export const speakerLabels: Record<string, string> = {
  customer: "Customer",
  agent: "Agent",
  unknown: "Unknown",
};

export const timelineKindLabels: Record<string, string> = {
  event: "Event",
  feedback: "Feedback",
  support: "Support",
  voice: "Call",
  insight: "Analysis AI",
};

export function label(
  map: Record<string, string>,
  key?: string | null,
): string {
  if (!key) return "—";
  return map[key] ?? key;
}

/** System intent vocabulary (Cx.Contracts.Ai.Intents); operators never edit it. */
export const intentLabels: Record<string, string> = {
  technical_support: "Technical support",
  billing_inquiry: "Financial question",
  complaint: "Complaint",
  feature_request: "Capability Request",
  cancellation: "Cancellation of service",
  information_request: "Information request",
  praise: "Thanks and praise",
  unknown: "Unknown",
};
export const channelOptions = [
  "manual",
  "phone",
  "in_person",
  "email",
  "web",
  "social",
  "voice",
  "mobile",
  "internal",
];
export const channelLabels: Record<string, string> = {
  manual: "Manual entry", phone: "Tel", in_person: "In person", email: "Email",
  web: "Website", social: "Social network", voice: "Audio", mobile: "Mobile app", internal: "Internal",
};
