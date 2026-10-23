import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import Pagination from "../components/Pagination";
import { Fragment, useMemo, useState } from "react";
import { api, type TopicInput, type TopicNode } from "../api";
import { Empty, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import Icon from "../components/Icon";
import Modal from "../components/Modal";
import { fmtDateTime, fmtNumber } from "../format";
import { flattenTopics, invalidateTopics, type TopicOption } from "../hooks/useTopics";

/** Mirrors the server limit (TopicEndpoints.MaxDepth). */
const MAX_DEPTH = 4;

type Editing = { mode: "create"; parentId: string | null } | { mode: "edit"; topic: TopicNode };

/**
 * The topic tree operators maintain. The AI reads these rows for every analysis and picks
 * the most specific matching topic, so what an operator writes here — titles, what belongs
 * under each topic, example phrases — is exactly what the classification is based on.
 */
export default function Taxonomy() {
  const { data, error, loading, reload } = useAsync(() => api.topics(), []);
  const [query, setQuery] = useState("");
  const [showInactive, setShowInactive] = useState(true);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [editing, setEditing] = useState<Editing | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [paging, setPaging] = useState({ page: 1, size: 20, source: "" });

  const options = useMemo(() => flattenTopics(data?.topics ?? []), [data]);
  const childCount = useMemo(() => {
    const counts = new Map<string, number>();
    for (const t of data?.topics ?? [])
      if (t.parent_id) counts.set(t.parent_id, (counts.get(t.parent_id) ?? 0) + 1);
    return counts;
  }, [data]);

  const visible = useMemo(
    () => visibleRows(options, query.trim(), showInactive, collapsed),
    [options, query, showInactive, collapsed],
  );

  const canManage = Boolean(data?.can_manage);
  const rootCount = options.filter((o) => o.depth === 0).length;
  const roots = visible.filter(option => option.depth === 0).map(option => option.topic.id);
  const source = `${query.trim()}|${showInactive}|${roots.join(",")}`;
  const page = source === paging.source ? Math.min(paging.page, Math.max(1, Math.ceil(roots.length / paging.size))) : 1;
  const selectedRoots = new Set(roots.slice((page - 1) * paging.size, page * paging.size));
  const byId = new Map(options.map(option => [option.topic.id, option.topic]));
  const pageRows = visible.filter(({ topic }) => {
    let root = topic;
    const seen = new Set<string>();
    while (root.parent_id && byId.has(root.parent_id) && !seen.has(root.id)) { seen.add(root.id); root = byId.get(root.parent_id)!; }
    return selectedRoots.has(root.id);
  });

  function changed(message: string) {
    setNotice(message);
    setActionError(null);
    invalidateTopics();
    reload();
  }

  async function remove(topic: TopicNode) {
    const confirmed = window.confirm(
      `Topic «${topic.title}» Remove?`,
    );
    if (!confirmed) return;
    setBusyId(topic.id);
    setActionError(null);
    try {
      const result = await api.deleteTopic(topic.id);
      changed(
        result.deactivated
          ? `«${topic.title}» In ${fmtNumber(result.usage_count)} Analysis was used and disabled.`
          : `«${topic.title}» Deleted.`,
      );
    } catch (err) {
      setActionError(err);
    } finally {
      setBusyId(null);
    }
  }

  async function toggleActive(topic: TopicNode) {
    setBusyId(topic.id);
    setActionError(null);
    try {
      await api.updateTopic(topic.id, { ...toInput(topic), is_active: !topic.is_active });
      changed(
        topic.is_active
          ? `«${topic.title}» Disabled.`
          : `«${topic.title}» Reactivated.`,
      );
    } catch (err) {
      setActionError(err);
    } finally {
      setBusyId(null);
    }
  }

  function toggle(id: string) {
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  return (
    <>
      <PageHead
        title="Topic categories"
        actions={
          canManage && (
            <button className="primary" onClick={() => setEditing({ mode: "create", parentId: null })}>
              <Icon name="plus" size={16} /> New main topic
            </button>
          )
        }
      />

      <ErrorBox error={actionError} />
      {notice && (
        <div className="notice" role="status">
          <Icon name="check" size={16} />
          <span style={{ flex: 1 }}>{notice}</span>
          <button className="icon-button" aria-label="Close" onClick={() => setNotice(null)}>
            <Icon name="close" size={14} />
          </button>
        </div>
      )}

      <GridFilters>
        <label className="grid-filter"><span>Search topics</span><div className="grid-search">
          <Icon name="search" size={16} />
          <input
            aria-label="Search topics"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search in title, description or sample sentences…"
          />
          {query && (
            <button className="icon-button" aria-label="Clear search" onClick={() => setQuery("")}>
              <Icon name="close" size={14} />
            </button>
          )}
        </div></label>
        <label className="inline-check">
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
          Display inactive topics
        </label>
        <button onClick={() => setCollapsed(new Set())} disabled={Boolean(query)}>
          Open all
        </button>
        <button
          onClick={() => setCollapsed(new Set(options.filter((o) => childCount.has(o.topic.id)).map((o) => o.topic.id)))}
          disabled={Boolean(query)}
        >
          Close all
        </button>
      </GridFilters>

      <GridPanel>
        <ErrorBox error={error} />
        {loading && <Loading />}
        {data && rootCount === 0 && (
          <Empty text="The subject is not defined." />
        )}
        {data && rootCount > 0 && visible.length === 0 && <Empty text="No subject was found with this search." />}
        {visible.length > 0 && (
          <div className="table-scroll">
            <DataTable paginate={false} className="topic-grid">
              <thead>
                <tr>
                  <th>Topic</th>
                  <th>Explanation</th>
                  <th>Feedback</th>
                  <th>Status</th>
                  <th>Last change</th>
                  {canManage && <th aria-label="Operation" />}
                </tr>
              </thead>
              <tbody>
                {pageRows.map(({ topic, depth }) => {
                  const children = childCount.get(topic.id) ?? 0;
                  const isCollapsed = !query && collapsed.has(topic.id);
                  return (
                    <tr key={topic.id} className={topic.is_active ? undefined : "inactive"}>
                      <td>
                        <div className="topic-cell" style={{ paddingInlineStart: depth * 24 }}>
                          {children > 0 ? (
                            <button
                              className="tree-toggle"
                              aria-label={isCollapsed ? "Show subtopics" : "Hide subtopics"}
                              aria-expanded={!isCollapsed}
                              onClick={() => toggle(topic.id)}
                              disabled={Boolean(query)}
                            >
                              <Icon name="chevron" size={14} style={isCollapsed ? { transform: "rotate(90deg)" } : undefined} />
                            </button>
                          ) : (
                            <span className="tree-leaf" aria-hidden />
                          )}
                          <div>
                            <strong>
                              <Highlight text={topic.title} query={query} />
                            </strong>
                            {children > 0 && (
                              <small className="muted"> · {fmtNumber(children)} Subtopic</small>
                            )}
                          </div>
                        </div>
                      </td>
                      <td className="topic-guidance">
                        {topic.description ? (
                          <p>
                            <Highlight text={topic.description} query={query} />
                          </p>
                        ) : (
                          <span className="muted">—</span>
                        )}
                        {topic.keywords.length > 0 && (
                          <div className="chips">
                            {topic.keywords.slice(0, 4).map((k) => (
                              <span key={k} className="chip">
                                <Highlight text={k} query={query} />
                              </span>
                            ))}
                            {topic.keywords.length > 4 && (
                              <span className="chip muted">+{fmtNumber(topic.keywords.length - 4)}</span>
                            )}
                          </div>
                        )}
                      </td>
                      <td className="nowrap">{fmtNumber(topic.usage_count)}</td>
                      <td>
                        <span className={`badge ${topic.is_active ? "ok" : ""}`}>
                          {topic.is_active ? "Enabled" : "Disabled"}
                        </span>
                      </td>
                      <td className="small muted nowrap">
                        {fmtDateTime(topic.updated_at)}
                        {topic.updated_by && <div>{topic.updated_by}</div>}
                      </td>
                      {canManage && (
                        <td>
                          <div className="row-actions">
                            {depth + 1 < MAX_DEPTH && (
                              <button
                                className="icon-button"
                                title="Add subtopic"
                                aria-label={`Add subtopic to ${topic.title}`}
                                onClick={() => setEditing({ mode: "create", parentId: topic.id })}
                              >
                                <Icon name="plus" size={15} />
                              </button>
                            )}
                            <button
                              className="icon-button"
                              title="Edit"
                              aria-label={`Edit ${topic.title}`}
                              onClick={() => setEditing({ mode: "edit", topic })}
                            >
                              <Icon name="edit" size={15} />
                            </button>
                            <button
                              className="icon-button"
                              title={topic.is_active ? "Disable" : "Activate"}
                              aria-label={`${topic.is_active ? "Disable" : "Activate"} ${topic.title}`}
                              disabled={busyId === topic.id}
                              onClick={() => toggleActive(topic)}
                            >
                              <Icon name={topic.is_active ? "eye-off" : "eye"} size={15} />
                            </button>
                            <button
                              className="icon-button danger"
                              title="Delete"
                              aria-label={`Delete ${topic.title}`}
                              disabled={busyId === topic.id}
                              onClick={() => remove(topic)}
                            >
                              <Icon name="trash" size={15} />
                            </button>
                          </div>
                        </td>
                      )}
                    </tr>
                  );
                })}
              </tbody>
            </DataTable>
          </div>
        )}
        {data && <Pagination page={page} pageSize={paging.size} total={roots.length} unit="Main topic" sizeLabel="The main topic on each page"
          onPage={value => setPaging({ ...paging, page: value, source })} onPageSize={size => setPaging({ page: 1, size, source })} />}
      </GridPanel>

      {editing && data && (
        <Modal
          title={
            editing.mode === "edit"
              ? `Edit «${editing.topic.title}»`
              : editing.parentId
                ? "New subtopic"
                : "New main topic"
          }
          onClose={() => setEditing(null)}
          wide
        >
          <TopicForm
            editing={editing}
            options={options}
            onCancel={() => setEditing(null)}
            onSaved={(saved, created) => {
              setEditing(null);
              if (saved.parent_id)
                setCollapsed((current) => {
                  const next = new Set(current);
                  next.delete(saved.parent_id!);
                  return next;
                });
              changed(created ? `«${saved.title}» Added.` : `Changes «${saved.title}» Saved.`);
            }}
          />
        </Modal>
      )}
    </>
  );
}

function TopicForm({
  editing,
  options,
  onSaved,
  onCancel,
}: {
  editing: Editing;
  options: TopicOption[];
  onSaved: (topic: TopicNode, created: boolean) => void;
  onCancel: () => void;
}) {
  const topic = editing.mode === "edit" ? editing.topic : undefined;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  // A topic cannot move under itself or its own branch, nor below the depth limit.
  const excluded = useMemo(() => {
    if (!topic) return new Set<string>();
    const out = new Set<string>([topic.id]);
    let grew = true;
    while (grew) {
      grew = false;
      for (const o of options)
        if (o.topic.parent_id && out.has(o.topic.parent_id) && !out.has(o.topic.id)) {
          out.add(o.topic.id);
          grew = true;
        }
    }
    return out;
  }, [topic, options]);
  const subtreeHeight = useMemo(() => {
    if (!topic) return 0;
    const own = options.find((o) => o.topic.id === topic.id)?.depth ?? 0;
    return Math.max(0, ...options.filter((o) => excluded.has(o.topic.id)).map((o) => o.depth - own));
  }, [topic, options, excluded]);
  const parents = options.filter((o) => !excluded.has(o.topic.id) && o.depth + 1 + subtreeHeight < MAX_DEPTH);

  const initialParent = topic ? (topic.parent_id ?? "") : editing.mode === "create" ? (editing.parentId ?? "") : "";

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    event.stopPropagation();
    const form = new FormData(event.currentTarget);
    const text = (name: string) => String(form.get(name) ?? "").trim();
    const order = text("sort_order");
    const body: TopicInput = {
      title: text("title"),
      parent_id: text("parent_id") || null,
      description: text("description") || null,
      keywords: text("keywords")
        .split("\n")
        .map((line) => line.trim())
        .filter(Boolean),
      sort_order: order === "" ? undefined : Number(order),
      is_active: form.get("is_active") === "on",
    };
    if (!body.title) {
      setError(new Error("Enter the subject title."));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const saved = topic ? await api.updateTopic(topic.id, body) : await api.createTopic(body);
      onSaved(saved, !topic);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="topic-form">
      <ErrorBox error={error} />
      <fieldset disabled={busy}>
        <div className="form-grid">
          <label className="field">
            <span>
              Subject title <span className="required">*</span>
            </span>
            <input name="title" required maxLength={200} defaultValue={topic?.title ?? ""} autoFocus />
          </label>
          <label className="field">
            <span>Subset</span>
            <select name="parent_id" defaultValue={initialParent}>
              <option value="">— Main topic (No parent) —</option>
              {parents.map((o) => (
                <option key={o.topic.id} value={o.topic.id}>
                  {"   ".repeat(o.depth)}
                  {o.topic.title}
                  {o.topic.is_active ? "" : " (Disabled)"}
                </option>
              ))}
            </select>
          </label>
        </div>
        <label className="field">
          <span>Explanation</span>
          <textarea
            name="description"
            rows={3}
            maxLength={2000}
            defaultValue={topic?.description ?? ""}
          />
        </label>
        <label className="field">
          <span>Examples of customer sentences</span>
          <textarea
            name="keywords"
            rows={4}
            defaultValue={topic?.keywords.join("\n") ?? ""}
          />
        </label>
        <div className="form-grid">
          <label className="field">
            <span>Display order</span>
            <input
              name="sort_order"
              type="number"
              inputMode="numeric"
              dir="ltr"
              defaultValue={topic?.sort_order ?? ""}
              placeholder="Automatic"
            />
          </label>
          <label className="field inline-check">
            <input name="is_active" type="checkbox" defaultChecked={topic?.is_active ?? true} />
            <span>Enabled</span>
          </label>
        </div>
        <div className="form-submit">
          <button className="primary" type="submit">
            <Icon name="check" size={16} />
            {busy ? "Saving…" : topic ? "Save changes" : "Add subject"}
          </button>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </fieldset>
    </form>
  );
}

/** Rows to render: search shows matches with their ancestors; otherwise collapsed branches are hidden. */
function visibleRows(
  options: TopicOption[],
  query: string,
  showInactive: boolean,
  collapsed: Set<string>,
): TopicOption[] {
  const byId = new Map(options.map((o) => [o.topic.id, o]));
  const ancestors = (o: TopicOption) => {
    const out: string[] = [];
    for (let p = o.topic.parent_id; p && byId.has(p) && !out.includes(p); p = byId.get(p)!.topic.parent_id)
      out.push(p);
    return out;
  };
  // An inactive parent retires its whole branch, so hiding inactive hides the branch too.
  const effectiveActive = (o: TopicOption) =>
    o.topic.is_active && ancestors(o).every((id) => byId.get(id)!.topic.is_active);

  let rows = showInactive ? options : options.filter(effectiveActive);

  if (query) {
    const q = query.toLowerCase();
    const matches = rows.filter(({ topic }) =>
      [topic.title, topic.description ?? "", ...topic.keywords].some((v) => v.toLowerCase().includes(q)),
    );
    const keep = new Set<string>();
    for (const m of matches) {
      keep.add(m.topic.id);
      ancestors(m).forEach((id) => keep.add(id));
    }
    return rows.filter((o) => keep.has(o.topic.id));
  }

  rows = rows.filter((o) => !ancestors(o).some((id) => collapsed.has(id)));
  return rows;
}

function toInput(topic: TopicNode): TopicInput {
  return {
    parent_id: topic.parent_id ?? null,
    title: topic.title,
    description: topic.description ?? null,
    keywords: topic.keywords,
    sort_order: topic.sort_order,
    is_active: topic.is_active,
  };
}

function Highlight({ text, query }: { text: string; query: string }) {
  const q = query.trim();
  if (!q) return <>{text}</>;
  const index = text.toLowerCase().indexOf(q.toLowerCase());
  if (index < 0) return <>{text}</>;
  return (
    <Fragment>
      {text.slice(0, index)}
      <mark>{text.slice(index, index + q.length)}</mark>
      {text.slice(index + q.length)}
    </Fragment>
  );
}
