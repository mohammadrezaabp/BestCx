export type InboxKind = "feedback" | "complaints";

export function inboxFor(feedbackType: string): InboxKind {
  return feedbackType === "complaint" ? "complaints" : "feedback";
}

export function inboxPath(inbox: InboxKind): string {
  return inbox === "complaints" ? "/complaints" : "/experience";
}

/** Older portal submissions repeat their structured metadata before the customer's words. */
export function feedbackComment(comment?: string | null): string {
  return (comment ?? "").replace(
    /^Type of request: [^\r\n]*\r?\nSelected topic: [^\r\n]*(?:\r?\n)+/u,
    "",
  ).trim();
}
