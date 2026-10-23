import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { Fragment, useState } from "react";
import { api } from "../api";
import { Empty, ErrorBox, Loading, PageHead, Pager, useAsync, useCursorPaging } from "../components/Common";
import { fmtDateTime } from "../format";

const actionNames: Record<string, string> = {
  "case.created": "File registration", "case.updated": "Editing file specifications", "case.workflow": "Changing the processing stage", "case.message": "Expert answer", "case.customer_message": "Customer response", "case.status": "Changing the case status", "case.attachment": "Attached registration",
  "user.profile.update": "Personal account editing", "ai.override": "Historical modification of the analysis", "ai.approve": "Historical verification of analysis", "ai.reject": "Historical rejection of analysis",
  "transcript.edit": "Modification of the contact text", "audio.access": "Audio access", export: "Receive output", reprocess: "Reprocessing",
  "topic.create": "Creating a topic", "topic.update": "Edit topic", "topic.delete": "Delete the topic", "taxonomy.publish": "Dissemination of subjects", "identity.merge": "Customer integration", "feedback.update": "Historical feedback editing",
};
const actions = Object.keys(actionNames);
export default function AuditLog() {
  const [action, setAction] = useState("");
  const [actor, setActor] = useState("");
  const [applied, setApplied] = useState("");
  const [expanded, setExpanded] = useState<number | null>(null);
  const paging = useCursorPaging();

  const { data, error, loading } = useAsync(
    () =>
      api.audit({
        action: action || undefined,
        actorId: applied || undefined,
        cursor: paging.cursor,
        limit: paging.pageSize,
      }),
    [action, applied, paging.cursor, paging.pageSize],
  );

  return (
    <>
      <PageHead
        title="Audit report"
      />

      <GridFilters as="form" onSubmit={event => { event.preventDefault(); paging.reset(); setApplied(actor.trim()); }}>
        <label className="grid-filter"><span>Type of activity</span><select value={action} onChange={event => { paging.reset(); setAction(event.target.value); }}><option value="">All activities</option>{actions.map(value => <option key={value} value={value}>{actionNames[value] ?? value}</option>)}</select></label>
        <label className="grid-filter"><span>User</span><input placeholder="Username…" value={actor} onChange={event => setActor(event.target.value)} /></label>
        <div className="toolbar"><button className="primary" type="submit">Search</button><button type="button" onClick={() => { setAction(""); setActor(""); setApplied(""); paging.reset(); }}>Clear filters</button></div>
      </GridFilters>
      <ErrorBox error={error} />

      <GridPanel>
        {loading && <Loading />}
        {data && data.items.length === 0 && <Empty text="No record recorded" />}

        {data && data.items.length > 0 && (
          <DataTable paginate={false}>
            <thead>
              <tr>
                <th>Time</th>
                <th>User</th>
                <th>Operation</th>
                <th>Source</th>
                <th>Reason</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.items.map((entry) => (
                <Fragment key={entry.id}>
                  <tr key={entry.id}>
                    <td className="small muted">
                      {fmtDateTime(entry.occurred_at)}
                    </td>
                    <td className="mono">{entry.actor_id}</td>
                    <td>
                      <span className="badge">{entry.action}</span>
                    </td>
                    <td
                      className="mono small"
                      style={{ maxWidth: 240, wordBreak: "break-all" }}
                    >
                      {entry.resource_type}/{entry.resource_id}
                    </td>
                    <td className="small">{entry.reason ?? "—"}</td>
                    <td>
                      {(entry.before || entry.after) && (
                        <button
                          style={{ minWidth: "auto" }}
                          onClick={() =>
                            setExpanded(expanded === entry.id ? null : entry.id)
                          }
                        >
                          {expanded === entry.id ? "Close" : "Changes"}
                        </button>
                      )}
                    </td>
                  </tr>
                  {expanded === entry.id && (
                    <tr key={`${entry.id}-detail`}>
                      <td colSpan={6}>
                        {/* before/after are stored so a decision can always be reconstructed. */}
                        <div className="grid cols-2">
                          <div>
                            <div className="small muted">Before</div>
                            <pre
                              className="mono small"
                              style={{ whiteSpace: "pre-wrap", margin: 0 }}
                            >
                              {pretty(entry.before)}
                            </pre>
                          </div>
                          <div>
                            <div className="small muted">Next</div>
                            <pre
                              className="mono small"
                              style={{ whiteSpace: "pre-wrap", margin: 0 }}
                            >
                              {pretty(entry.after)}
                            </pre>
                          </div>
                        </div>
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </DataTable>
        )}

        <Pager page={paging.page} pageSize={paging.pageSize} onPageSize={paging.setPageSize}
          hasMore={Boolean(data?.has_more)}
          canGoBack={paging.canGoBack}
          onNext={() => paging.next(data?.next_cursor)}
          onBack={paging.back}
        />
      </GridPanel>
    </>
  );
}

function pretty(value?: string | null): string {
  if (!value) return "—";
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}
