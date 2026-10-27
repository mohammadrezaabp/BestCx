import { useRef, useState } from "react";
import { ApiError, api, type CustomerIdentity } from "../api";
import { useTopics, invalidateTopics } from "../hooks/useTopics";
import { ErrorBox, Loading } from "./Common";
import CustomerPicker from "./CustomerPicker";
import TopicSelects from "./TopicSelects";

export default function ComplaintForm({ onSaved, onCancel }: { onSaved: () => void; onCancel: () => void }) {
  const topics = useTopics();
  const [customer, setCustomer] = useState<CustomerIdentity | null>(null);
  const [parentCode, setParentCode] = useState("");
  const [childCode, setChildCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const request = useRef<{ signature: string; id: string } | undefined>(undefined);
  // Prune inactive branches exactly as the server does.
  const byId = new Map(topics.options.map(option => [option.topic.id, option.topic]));
  const active = topics.options.filter(({ topic }) => {
    const seen = new Set<string>();
    for (let current: typeof topic | undefined = topic; current; current = current.parent_id ? byId.get(current.parent_id) : undefined) {
      if (!current.is_active || seen.has(current.id)) return false;
      seen.add(current.id);
      if (current.parent_id && !byId.has(current.parent_id)) return false;
    }
    return true;
  });
  const parents = active.filter(({ depth }) => depth === 0);
  const parent = parents.find(({ topic }) => topic.code === parentCode);
  const activeIds = new Set(active.map(({ topic }) => topic.id));
  const children = topics.branchOf(parent?.topic.code).filter(({ topic }) => activeIds.has(topic.id));
  const child = children.find(({ topic }) => topic.code === childCode);

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    if (!customer) { setError(new Error("Select the client.")); return; }
    if (!child) { setError(new Error("Select the problem subcategory.")); return; }
    const fields = {
      customer_id: customer.customer_id,
      category: child.topic.code,
      revision: topics.tree!.revision,
      comment: String(form.get("comment") ?? "").trim(),
    };
    if (!fields.comment) { setError(new Error("Enter the text of the complaint.")); return; }
    const signature = JSON.stringify(fields);
    if (request.current?.signature !== signature) request.current = { signature, id: crypto.randomUUID() };
    setBusy(true);
    setError(null);
    try {
      await api.submitComplaint({ ...fields, submission_id: request.current.id });
      onSaved();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) invalidateTopics();
      setError(err);
    }
    finally { setBusy(false); }
  }

  if (topics.loading) return <Loading />;
  if (topics.error) return <><ErrorBox error={topics.error} /><button onClick={invalidateTopics}>Retry</button></>;
  if (!active.length) return <p role="status">There is no active category. First, define the problem category in the management of issues.</p>;
  return (
    <form className="customer-form" onSubmit={submit}>
      <ErrorBox error={error} />
      <fieldset disabled={busy}>
        <div className="field"><span>Customer</span><CustomerPicker value={customer} onChange={setCustomer} disabled={busy} /></div>
        <TopicSelects options={active} parentValue={parent?.topic.code ?? ""} childValue={child?.topic.code ?? ""}
          className="field" required onParentChange={value => {
            setParentCode(value);
            setChildCode("");
          }} onChildChange={setChildCode} />
        {parent && !children.length && <p role="status">This category does not have an active subcategory. First, define the subcategory in the management of topics.</p>}
        <label className="field">
          <span>The text of the complaint</span>
          <textarea name="comment" required maxLength={12000} rows={6} dir="auto" />
        </label>
        <div className="form-submit">
          <button className="primary" type="submit" disabled={!child}>{busy ? "Registering…" : "Submit complaint"}</button>
          <button type="button" onClick={onCancel}>Cancel</button>
        </div>
      </fieldset>
    </form>
  );
}
