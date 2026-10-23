export interface Question {
  id: string;
  label: string;
  type: "rating" | "effort" | "text";
  required: boolean;
}
export interface Category {
  id: string;
  title: string;
  path: string;
  description?: string;
  questions: Question[];
}
export interface Catalog {
  revision: number;
  categories: Category[];
}
export type Kind = "complaint" | "feedback" | "voc";
export interface Submission {
  submission_id: string;
  kind: Kind;
  category: string;
  revision: number;
  subject: string;
  comment: string;
  rating: number | null;
  nps_score: number | null;
  ces_score: number | null;
  answers: Record<string, string>;
}
export interface PortalUser {
  customer_id: string;
  username: string;
  display_name: string;
}
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message);
  }
}
export async function request<T>(
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(`/v1/portal/${path}`, {
    method: body ? "POST" : "GET",
    credentials: "same-origin",
    signal,
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  return read<T>(response);
}

/** A complaint or feedback told by voice: the form answers travel as JSON beside the audio. */
export async function submitVoice<T>(
  submission: Submission,
  audio: { blob: Blob; durationMs?: number },
): Promise<T> {
  const form = new FormData();
  form.append("submission", JSON.stringify(submission));
  if (audio.durationMs) form.append("duration_ms", String(audio.durationMs));
  // The server reads the part's content type, not the file name.
  form.append("audio", audio.blob, "message");
  const response = await fetch("/v1/portal/voice", {
    method: "POST",
    credentials: "same-origin",
    body: form,
  });
  return read<T>(response);
}

async function read<T>(response: Response): Promise<T> {
  const data = await response.json().catch(() => null);
  if (response.status === 401)
    window.dispatchEvent(new Event("portal-session-expired"));
  if (!response.ok)
    throw new ApiError(
      response.status === 429
        ? "The number of requests is high; Try another minute."
        : response.status === 413
          ? data?.error || "The voice message is too large; Record shorter."
          : data?.error ||
            (response.status === 401
              ? "Log in to your account to continue."
              : "Communication with the system was not established; Try again."),
      response.status,
    );
  return data as T;
}

export async function uploadCaseFile(caseId: string, file: File, attachmentId: string) {
  const body = new FormData(); body.append("file", file); body.append("attachment_id", attachmentId);
  return read(await fetch(`/v1/portal/cases/${caseId}/attachments`, { method: "POST", body, credentials: "same-origin" }));
}
