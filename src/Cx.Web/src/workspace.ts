import type { InboxKind } from "./feedback";

export type Workspace = "complaints" | "experience";
export const workspaceLabel = (workspace: Workspace) => workspace === "complaints" ? "Complaints" : "Customer experience";
export const workspaceInbox = (workspace: Workspace): InboxKind => workspace === "complaints" ? "complaints" : "feedback";
export const workspacePath = (workspace: Workspace, page = "") => `/${workspace}${page ? `/${page}` : ""}`;
export const voicePath = (inbox: InboxKind, callId?: string) => workspacePath(inbox === "complaints" ? "complaints" : "experience", `voice${callId ? `/${encodeURIComponent(callId)}` : ""}`);
export const casePath = (id?: string) => workspacePath("complaints", `cases${id ? `/${encodeURIComponent(id)}` : ""}`);
